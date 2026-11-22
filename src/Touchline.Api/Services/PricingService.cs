using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>
/// Loads the data for the valuation formula (Fantasy/PlayerValuation.cs) and saves the results.
/// Prices are calculated once per PSL transfer window, when an admin runs it.
/// </summary>
public sealed class PricingService(
    TouchlineDbContext db,
    MatchFactsService facts,
    GameweekService gameweeks,
    FootballService football,
    IOptions<FantasyRules> options,
    TimeProvider time)
{
    private readonly FantasyRules _rules = options.Value;
    private readonly PlayerScoring _scoring = new(options.Value);
    private readonly PlayerValuation _valuation = new(options.Value.Pricing);

    // ---- Transfer windows and overrides ---------------------------------------------------------

    public async Task<TransferWindow> CreateWindowAsync(CreateTransferWindowRequest request, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 50) errors.Add("Give the window a name of 1 to 50 characters.");
        if (request.ClosesOn < request.OpensOn) errors.Add("The window can't close before it opens.");
        if (!await db.Seasons.AnyAsync(s => s.Id == request.SeasonId, ct)) errors.Add($"Season {request.SeasonId} doesn't exist.");
        if (errors.Count > 0) throw new RuleViolationException(errors);

        var window = new TransferWindow { SeasonId = request.SeasonId, Name = request.Name.Trim(), OpensOn = request.OpensOn, ClosesOn = request.ClosesOn };
        db.TransferWindows.Add(window);
        await db.SaveChangesAsync(ct);
        return window;
    }

    public Task<List<TransferWindow>> ListWindowsAsync(int? seasonId, CancellationToken ct) =>
        db.TransferWindows.Where(w => seasonId == null || w.SeasonId == seasonId).OrderBy(w => w.OpensOn).ToListAsync(ct);

    /// <summary>Insert only: the newest override per player and season wins. Every override needs a reason.</summary>
    public async Task<PlayerPriceOverride> AddOverrideAsync(int playerId, PriceOverrideRequest request, int adminUserId, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Reason)) errors.Add("Every override needs a reason.");
        if (request.ExpectedMinutes is null && request.Price is null) errors.Add("Set expectedMinutes, price, or both.");
        if (request.ExpectedMinutes is < 0) errors.Add("Expected minutes can't be negative.");
        if (request.Price is { } p && (p <= 0 || p > 99.9m || p * 10 != Math.Truncate(p * 10))) errors.Add("Price must be between 0.1 and 99.9, with one decimal.");
        if (!await db.Players.AnyAsync(x => x.Id == playerId, ct)) errors.Add($"Player {playerId} doesn't exist.");
        if (!await db.Seasons.AnyAsync(s => s.Id == request.SeasonId, ct)) errors.Add($"Season {request.SeasonId} doesn't exist.");
        if (errors.Count > 0) throw new RuleViolationException(errors);

        var row = new PlayerPriceOverride
        {
            PlayerId = playerId,
            SeasonId = request.SeasonId,
            ExpectedMinutes = request.ExpectedMinutes,
            Price = request.Price,
            Reason = request.Reason.Trim(),
            CreatedByUserId = adminUserId,
        };
        db.PriceOverrides.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    // ---- Preview and recalculation ------------------------------------------------------------

    /// <summary>
    /// 12i: prices for season N exactly as a pre-season run would make them (from N-1, N-2, N-3),
    /// without saving. For a past season with results, adds a backtest per position.
    /// </summary>
    public async Task<PricingReport> PreviewAsync(int seasonId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var run = await CalculateAsync(seasonId, midSeason: false, now, ct);

        IReadOnlyList<PositionBacktest>? backtest = null;
        var target = await SeasonStatsAsync(seasonId, now, ct);
        if (target.FinishedFixtures > 0)
        {
            backtest = PositionNames.Playable.Select(position => PlayerValuation.Backtest(position, run.Results
                .Where(r => r.Position == position && target.ByPlayer.TryGetValue(r.PlayerId, out var real) && real.Minutes > 0)
                .Select(r => (r.Price, target.ByPlayer[r.PlayerId].Points))
                .ToList())).ToList();
        }

        return Report(run, "preview (pre-season)", saved: false, effectiveGameweekId: null, changed: 0, backtest);
    }

    /// <summary>12a/12h: the once-per-window price run. Everything is calculated first, then saved in one transaction.</summary>
    public async Task<PricingReport> RecalculateAsync(int windowId, bool force, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var window = await db.TransferWindows.AsTracking().FirstOrDefaultAsync(w => w.Id == windowId, ct)
            ?? throw new NotFoundException($"Transfer window {windowId} doesn't exist.");

        var calendar = await gameweeks.GetCalendarAsync(window.SeasonId, ct);
        var first = calendar.Gameweeks.FirstOrDefault();
        if (first?.Deadline is null)
        {
            throw new ConflictException("Create the season's gameweeks (with fixtures or deadlines) before calculating prices.", "No gameweeks");
        }

        // A window that opens before the Gameweek 1 deadline is the pre-season window.
        var opensUtc = new DateTimeOffset(window.OpensOn.ToDateTime(TimeOnly.MinValue), GameweekRules.Sast).ToUniversalTime();
        var preSeason = opensUtc < first.Deadline;

        var check = PlayerValuation.CanRecalculate(window.PricesCalculatedAt, force, preSeason, first.Deadline, now);
        if (!check.IsValid) throw new ConflictException(string.Join(" ", check.Errors), "Can't recalculate");

        // Pre-season prices apply from Gameweek 1; mid-season from the first gameweek whose deadline is after the window opens.
        var effective = preSeason ? first : calendar.Gameweeks.FirstOrDefault(g => g.Deadline > opensUtc)
            ?? throw new ConflictException("No gameweek has a deadline after this window opens, so there's nowhere for new prices to start.", "No gameweek");

        var run = await CalculateAsync(window.SeasonId, midSeason: !preSeason, now, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = (await db.PlayerPrices.AsTracking().Where(p => p.SeasonId == window.SeasonId).ToListAsync(ct))
            .GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.First());

        var changed = 0;
        foreach (var result in run.Results)
        {
            if (existing.TryGetValue(result.PlayerId, out var row))
            {
                if (row.Price == result.Price) continue;
                // Insert only: one history row per change.
                db.PlayerPriceChanges.Add(new PlayerPriceChange
                {
                    PlayerId = result.PlayerId,
                    SeasonId = window.SeasonId,
                    GameweekId = effective.Id,
                    OldPrice = row.Price,
                    NewPrice = result.Price,
                });
                row.Price = result.Price;
                changed++;
            }
            else
            {
                db.PlayerPrices.Add(new PlayerPrice { PlayerId = result.PlayerId, SeasonId = window.SeasonId, Price = result.Price });
            }
        }
        window.PricesCalculatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Report(run, preSeason ? "pre-season" : "mid-season", saved: true, effective.Id, changed, backtest: null);
    }

    // ---- Calculation ---------------------------------------------------------------------------

    private sealed record SeasonStats(
        int SeasonId,
        string Name,
        IReadOnlyDictionary<int, SeasonRecord> ByPlayer,
        IReadOnlyDictionary<int, DateTimeOffset> FirstAppearance,
        int MatchesPerTeam,
        int FinishedFixtures);

    private sealed record Run(
        int SeasonId,
        IReadOnlyList<ValuationResult> Results,
        IReadOnlyDictionary<int, string> Names,
        IReadOnlyDictionary<int, decimal> PreviousPrices,
        SeasonDataReport Data);

    private async Task<Run> CalculateAsync(int seasonId, bool midSeason, DateTimeOffset now, CancellationToken ct)
    {
        var seasons = await PslSeasonsAsync(ct);
        if (!seasons.Any(s => s.Id == seasonId))
        {
            throw new RuleViolationException($"Season {seasonId} isn't a PSL season (Sportmonks competition {_rules.Pricing.CompetitionSportmonksId}).");
        }

        // Newest seasons first; seasons with no lineup or event data are skipped (see SeasonsWithData).
        var history = new List<SeasonStats>();
        var skipped = new List<string>();
        var maxSeasons = _rules.Pricing.SeasonWeights().Count;
        foreach (var id in PlayerValuation.CandidateSeasons(seasons.Select(s => s.Id).ToList(), seasonId, midSeason))
        {
            if (history.Count == maxSeasons) break;
            var stats = await SeasonStatsAsync(id, now, ct);
            if (_valuation.SeasonsWithData([id], _ => stats.ByPlayer.Count > 0).Count > 0) history.Add(stats);
            else skipped.Add($"{stats.Name} ({stats.FinishedFixtures} final fixtures but no lineups or events)");
        }

        var players = await db.Players
            .Select(p => new { p.Id, p.PositionId, p.SportmonksPlayerId, p.DisplayName, p.CommonName, p.Firstname, p.Lastname })
            .ToListAsync(ct);
        var priced = players
            .Select(p => new { p.Id, p.SportmonksPlayerId, Position = PositionExtensions.FromSportmonks(p.PositionId),
                Name = FootballService.NameOf(new Player { DisplayName = p.DisplayName, CommonName = p.CommonName, Firstname = p.Firstname, Lastname = p.Lastname }) })
            .Where(p => p.Position != Position.Unknown)
            .ToList();

        var overrides = (await db.PriceOverrides.Where(o => o.SeasonId == seasonId).ToListAsync(ct))
            .GroupBy(o => o.PlayerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).First());

        var previous = (await db.PlayerPrices.Where(p => p.SeasonId == seasonId).ToListAsync(ct))
            .GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.First().Price);

        Func<int, long, decimal> expectedMinutes = midSeason
            ? await MidSeasonMinutesAsync(seasonId, history, ct)
            : (playerId, _) =>
            {
                var last = history.FirstOrDefault();
                var minutes = last is not null && last.ByPlayer.TryGetValue(playerId, out var r) ? r.Minutes : 0;
                return PlayerValuation.PreSeasonExpectedMinutes(minutes, last?.MatchesPerTeam ?? 0);
            };

        var inputs = priced.Select(p => new ValuationInput(
            p.Id,
            p.Position,
            history.Select(h => h.ByPlayer.TryGetValue(p.Id, out var r) ? r : null).ToList(),
            expectedMinutes(p.Id, p.SportmonksPlayerId),
            overrides.TryGetValue(p.Id, out var o) ? new PriceOverride(o.ExpectedMinutes, o.Price) : null)).ToList();

        var results = _valuation.PriceAll(inputs);
        return new Run(seasonId, results, priced.ToDictionary(p => p.Id, p => p.Name), previous, await DataReportAsync(seasons, history, skipped, ct));
    }

    /// <summary>
    /// 12d mid-season M: minutes per league match he was available for (his club's finished league
    /// matches since his first matchday squad this season) × his club's league matches left. Under
    /// 450 minutes this season, last season's M scaled to the matches left instead.
    /// </summary>
    private async Task<Func<int, long, decimal>> MidSeasonMinutesAsync(int seasonId, IReadOnlyList<SeasonStats> history, CancellationToken ct)
    {
        var current = history[0];
        var last = history.Count > 1 ? history[1] : null;

        var fixtures = await db.Fixtures.Where(f => f.SeasonId == seasonId && !f.IsPlaceholder).ToListAsync(ct);
        var clubs = await football.GetLatestClubsAsync(seasonId, ct);

        var byTeam = fixtures
            .SelectMany(f => new[] { f.HomeTeamId, f.AwayTeamId }.Where(t => t is not null).Select(t => (Team: t!.Value, Fixture: f)))
            .GroupBy(x => x.Team)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Fixture).ToList());

        static bool Remaining(Fixture f) => MatchStatusExtensions.FromStateId(f.StateId) is MatchStatus.Scheduled or MatchStatus.Live;
        var averageLeft = byTeam.Count == 0 ? 0 : (int)Math.Round(byTeam.Values.Average(list => list.Count(Remaining)));

        return (playerId, sportmonksId) =>
        {
            var minutes = current.ByPlayer.TryGetValue(playerId, out var r) ? r.Minutes : 0;
            var lastMinutes = last is not null && last.ByPlayer.TryGetValue(playerId, out var lr) ? lr.Minutes : 0;
            var lastSeasonM = PlayerValuation.PreSeasonExpectedMinutes(lastMinutes, last?.MatchesPerTeam ?? 0);

            int available = 0, left = averageLeft;
            if (clubs.TryGetValue(sportmonksId, out var club) && byTeam.TryGetValue(club, out var clubFixtures))
            {
                left = clubFixtures.Count(Remaining);
                if (current.FirstAppearance.TryGetValue(playerId, out var debut))
                {
                    available = clubFixtures.Count(f => MatchStatusExtensions.IsFinal(f.StateId) && f.StartingAt >= debut);
                }
            }

            return _valuation.MidSeasonExpectedMinutes(minutes, available, left, lastSeasonM, last?.MatchesPerTeam ?? 0);
        };
    }

    /// <summary>Base fantasy points (section 7 rules, no multipliers, chips or hits) and minutes per player, from final league fixtures.</summary>
    private async Task<SeasonStats> SeasonStatsAsync(int seasonId, DateTimeOffset now, CancellationToken ct)
    {
        var season = await db.Seasons.FirstAsync(s => s.Id == seasonId, ct);
        var fixtures = await db.Fixtures.Where(f => f.SeasonId == seasonId && !f.IsPlaceholder).ToListAsync(ct);
        var finished = fixtures.Where(f => MatchStatusExtensions.IsFinal(f.StateId)).ToList();
        var kickoffs = finished.ToDictionary(f => f.Id, f => f.StartingAt ?? DateTimeOffset.MinValue);

        var all = await facts.LoadAsync(finished, now, ct);
        var byPlayer = all.GroupBy(f => f.PlayerId).ToDictionary(
            g => g.Key,
            g => new SeasonRecord(
                g.Sum(x => (decimal)_scoring.ScoreFixture(x.Facts, x.Position).Total),
                g.Sum(x => PlayerScoring.Minutes(x.Facts))));
        var firstAppearance = all.GroupBy(f => f.PlayerId).ToDictionary(g => g.Key, g => g.Min(x => kickoffs[x.Facts.FixtureId]));

        var teams = fixtures.SelectMany(f => new[] { f.HomeTeamId, f.AwayTeamId }).Where(t => t is not null).Distinct().Count();
        var perTeam = teams == 0 ? 0 : (int)Math.Round(fixtures.Count * 2m / teams);

        return new SeasonStats(seasonId, season.Name, byPlayer, firstAppearance, perTeam, finished.Count);
    }

    private async Task<List<Season>> PslSeasonsAsync(CancellationToken ct)
    {
        var competitionId = _rules.Pricing.CompetitionSportmonksId;
        var seasons = await (from c in db.Competitions
                             join s in db.Seasons on c.Id equals s.CompetitionId
                             where c.SportmonksCompetitionId == competitionId
                             select s).ToListAsync(ct);
        return seasons.OrderBy(s => s.StartingAt ?? DateOnly.MaxValue).ThenBy(s => s.Id).ToList();
    }

    /// <summary>12i: how many past PSL seasons the database actually has.</summary>
    private async Task<SeasonDataReport> DataReportAsync(
        IReadOnlyList<Season> seasons, IReadOnlyList<SeasonStats> history, IReadOnlyList<string> skipped, CancellationToken ct)
    {
        var ids = seasons.Select(s => s.Id).ToList();
        var withResults = await db.Fixtures
            .Where(f => ids.Contains(f.SeasonId) && !f.IsPlaceholder && (f.StateId == SportmonksCodes.MatchStates.FullTime
                || f.StateId == SportmonksCodes.MatchStates.AfterExtraTime || f.StateId == SportmonksCodes.MatchStates.AfterPenalties))
            .Select(f => f.SeasonId)
            .Distinct()
            .ToListAsync(ct);

        var weights = _rules.Pricing.SeasonWeights().Count;
        var warnings = new List<string>();
        if (history.Count < weights)
        {
            warnings.Add($"Only {history.Count} PSL season(s) with lineup and event data feed this run (the formula uses up to {weights}). " +
                         "Most players will use reweighted or fallback prices.");
        }
        if (skipped.Count > 0)
        {
            warnings.Add($"Skipped for missing data: {string.Join("; ", skipped)}.");
        }

        return new SeasonDataReport(
            seasons.Count,
            seasons.Where(s => withResults.Contains(s.Id)).Select(s => s.Name).ToList(),
            history.Select(h => $"{h.Name} ({h.FinishedFixtures} final fixtures, {h.ByPlayer.Count} players with data)").ToList(),
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private static PricingReport Report(Run run, string kind, bool saved, int? effectiveGameweekId, int changed, IReadOnlyList<PositionBacktest>? backtest) => new(
        run.SeasonId,
        kind,
        saved,
        effectiveGameweekId,
        run.Results.Count,
        changed,
        run.Results.Count(r => r.FallbackPrice),
        run.Data,
        backtest,
        run.Results
            .OrderBy(r => r.Position).ThenByDescending(r => r.Price).ThenByDescending(r => r.ExpectedPoints)
            .Select(r => new PriceRowView(
                r.PlayerId,
                run.Names.GetValueOrDefault(r.PlayerId, $"Player {r.PlayerId}"),
                r.Position,
                r.ExpectedPoints is null ? null : Math.Round(r.ExpectedPoints.Value, 1),
                r.Percentile is null ? null : Math.Round(r.Percentile.Value, 4),
                r.Price,
                run.PreviousPrices.TryGetValue(r.PlayerId, out var p) ? p : null,
                Math.Round(r.ExpectedMinutes, 0),
                r.SeasonsUsed,
                r.FallbackPrice,
                r.PriceOverridden,
                r.MinutesOverridden))
            .ToList());
}

/// <summary>Admin checks. app.users.is_admin (docs/migrations/003) is read with its own query so /me works without 003.</summary>
public sealed class AdminService(TouchlineDbContext db)
{
    public async Task RequireAdminAsync(int userId, CancellationToken ct)
    {
        var isAdmin = await db.Database
            .SqlQuery<bool>($"SELECT is_admin AS \"Value\" FROM app.users WHERE id = {userId}")
            .FirstOrDefaultAsync(ct);
        if (!isAdmin) throw new ForbiddenException("Only admins can do this.");
    }
}
