using Touchline.Api.Business;

namespace Touchline.Api.Fantasy;

/// <summary>One player's base fantasy points and minutes in one past season (PSL league matches only).</summary>
public sealed record SeasonRecord(decimal Points, int Minutes);

/// <summary>An admin override. A price override wins over the formula; an expected-minutes override replaces M.</summary>
public sealed record PriceOverride(int? ExpectedMinutes, decimal? Price);

/// <param name="Seasons">Most recent first: [S1, S2, S3]. Null (or 0 minutes) means no data for that season.</param>
/// <param name="ExpectedMinutes">M, expected minutes next season, before any override.</param>
/// <param name="Priced">False for players who aren't available to pick: their history still counts
/// towards the position averages (12c), but they aren't ranked or priced.</param>
public sealed record ValuationInput(
    int PlayerId,
    Position Position,
    IReadOnlyList<SeasonRecord?> Seasons,
    decimal ExpectedMinutes,
    PriceOverride? Override = null,
    bool Priced = true);

/// <param name="SeasonsUsed">1 = S1 (last season, or this season on a mid-season run), 2 = S2, 3 = S3.</param>
public sealed record ValuationResult(
    int PlayerId,
    Position Position,
    decimal? ExpectedPoints,
    decimal? Percentile,
    decimal Price,
    decimal ExpectedMinutes,
    IReadOnlyList<int> SeasonsUsed,
    IReadOnlyList<decimal> WeightsUsed,
    bool FallbackPrice,
    bool PriceOverridden,
    bool MinutesOverridden);

/// <summary>
/// Touchline's own player valuation formula (section 12). Pure decimal maths, no database.
/// Data loading lives in Services/PricingService.
/// </summary>
public sealed class PlayerValuation(PricingRules pricing)
{
    /// <summary>12b: points per 90. Null when he played no minutes.</summary>
    public static decimal? PointsPer90(decimal points, int minutes) =>
        minutes <= 0 ? null : points * 90m / minutes;

    /// <summary>12c: pull R towards the position average. R_adj = (minutes × R + K × avg) ÷ (minutes + K).</summary>
    public static decimal Shrink(decimal r, int minutes, decimal positionAverage, decimal k) =>
        (minutes * r + k * positionAverage) / (minutes + k);

    /// <summary>12c: minutes-weighted average R of a position in a season.</summary>
    public static decimal PositionAverage(IEnumerable<SeasonRecord> records)
    {
        var played = records.Where(r => r.Minutes > 0).ToList();
        var minutes = played.Sum(r => r.Minutes);
        // Σ(minutes × R) = Σ(points × 90), so the weighted average is total points per 90.
        return minutes == 0 ? 0m : played.Sum(r => r.Points) * 90m / minutes;
    }

    /// <summary>
    /// 12d: weights for the seasons a player has, rescaled to add up to 1.
    /// E.g. with S1 and S2 only, 0.6 and 0.3 become 0.667 and 0.333.
    /// </summary>
    public IReadOnlyList<decimal> RescaledWeights(IReadOnlyList<bool> hasSeason)
    {
        var weights = pricing.SeasonWeights();
        var total = weights.Where((_, i) => i < hasSeason.Count && hasSeason[i]).Sum();
        return weights.Select((w, i) => i < hasSeason.Count && hasSeason[i] && total > 0 ? w / total : 0m).ToList();
    }

    /// <summary>12d: E = (Σ weight × R_adj) × M ÷ 90, using only the seasons present.</summary>
    public decimal ExpectedPoints(IReadOnlyList<decimal?> adjustedR, decimal expectedMinutes)
    {
        var weights = RescaledWeights(adjustedR.Select(r => r is not null).ToList());
        var perNinety = adjustedR.Select((r, i) => (r ?? 0m) * weights[i]).Sum();
        return perNinety * expectedMinutes / 90m;
    }

    /// <summary>12d pre-season: last season's minutes, capped at league matches × 90.</summary>
    public static decimal PreSeasonExpectedMinutes(int lastSeasonMinutes, int leagueMatchesPerTeam) =>
        Math.Min(lastSeasonMinutes, leagueMatchesPerTeam * 90);

    /// <summary>
    /// 12d mid-season: minutes per league match he was available for × matches left. Under the
    /// threshold (450 minutes), last season's M scaled to the matches left instead.
    /// </summary>
    public decimal MidSeasonExpectedMinutes(
        int minutesThisSeason,
        int matchesAvailable,
        int matchesLeft,
        decimal lastSeasonExpectedMinutes,
        int leagueMatchesPerTeamLastSeason)
    {
        if (minutesThisSeason < pricing.MidSeasonMinMinutes)
        {
            return leagueMatchesPerTeamLastSeason <= 0
                ? 0m
                : lastSeasonExpectedMinutes * matchesLeft / leagueMatchesPerTeamLastSeason;
        }
        return matchesAvailable <= 0 ? 0m : (decimal)minutesThisSeason / matchesAvailable * matchesLeft;
    }

    /// <summary>
    /// 12e: percentile within a position. Q = (rank − 1) ÷ (n − 1), rank 1 = lowest E; equal E share
    /// the lower rank; a lone player gets 0.5.
    /// </summary>
    public static IReadOnlyDictionary<int, decimal> Percentiles(IReadOnlyList<(int PlayerId, decimal E)> players)
    {
        if (players.Count == 1) return new Dictionary<int, decimal> { [players[0].PlayerId] = 0.5m };

        var n = players.Count;
        return players.ToDictionary(
            p => p.PlayerId,
            p => (decimal)players.Count(o => o.E < p.E) / (n - 1));
    }

    /// <summary>12e: V = step × round((min + (max − min) × Q) ÷ step), halves rounded up.</summary>
    public decimal Price(decimal percentile, Position position)
    {
        var range = pricing.RangeFor(position);
        var raw = range.Min + (range.Max - range.Min) * percentile;
        return RoundToStep(raw, pricing.PriceStep);
    }

    public static decimal RoundToStep(decimal value, decimal step) =>
        step * Math.Round(value / step, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Runs 12b–12g. Position averages use every input's history; only inputs with Priced = true are
    /// ranked (against their own position) and returned.
    /// </summary>
    public IReadOnlyList<ValuationResult> PriceAll(IReadOnlyList<ValuationInput> inputs)
    {
        var seasonCount = pricing.SeasonWeights().Count;

        // 12c: position averages per season, from everyone who played that season.
        var averages = new Dictionary<(Position, int), decimal>();
        foreach (var position in PositionNames.Playable)
        {
            for (var s = 0; s < seasonCount; s++)
            {
                var season = s;
                averages[(position, s)] = PositionAverage(inputs
                    .Where(i => i.Position == position && season < i.Seasons.Count && i.Seasons[season] is not null)
                    .Select(i => i.Seasons[season]!));
            }
        }

        var drafts = inputs.Where(i => i.Position != Position.Unknown && i.Priced).Select(input =>
        {
            var adjusted = new decimal?[seasonCount];
            for (var s = 0; s < seasonCount; s++)
            {
                var record = s < input.Seasons.Count ? input.Seasons[s] : null;
                if (record is null || PointsPer90(record.Points, record.Minutes) is not { } r) continue;
                adjusted[s] = Shrink(r, record.Minutes, averages[(input.Position, s)], pricing.ShrinkMinutes);
            }

            var minutesOverridden = input.Override?.ExpectedMinutes is not null;
            var m = minutesOverridden ? input.Override!.ExpectedMinutes!.Value : input.ExpectedMinutes;
            var hasHistory = adjusted.Any(a => a is not null);

            return new
            {
                Input = input,
                M = (decimal)m,
                MinutesOverridden = minutesOverridden,
                E = hasHistory ? ExpectedPoints(adjusted, m) : (decimal?)null,
                Seasons = adjusted.Select((a, i) => (a, i)).Where(x => x.a is not null).Select(x => x.i + 1).ToList(),
                Weights = RescaledWeights(adjusted.Select(a => a is not null).ToList()),
            };
        }).ToList();

        var percentiles = drafts
            .Where(d => d.E is not null)
            .GroupBy(d => d.Input.Position)
            .SelectMany(g => Percentiles(g.Select(d => (d.Input.PlayerId, d.E!.Value)).ToList()))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return drafts.Select(d =>
        {
            var hasQ = percentiles.TryGetValue(d.Input.PlayerId, out var q);
            var fallback = !hasQ;
            var formulaPrice = fallback ? pricing.RangeFor(d.Input.Position).Min : Price(q, d.Input.Position);
            var overridePrice = d.Input.Override?.Price;

            return new ValuationResult(
                d.Input.PlayerId,
                d.Input.Position,
                d.E,
                hasQ ? q : null,
                overridePrice ?? formulaPrice,
                d.M,
                d.Seasons,
                d.Weights,
                FallbackPrice: fallback && overridePrice is null,
                PriceOverridden: overridePrice is not null,
                MinutesOverridden: d.MinutesOverridden);
        }).ToList();
    }

    /// <summary>
    /// Which seasons feed S1, S2, S3. Pre-season run for season N: N-1, N-2, N-3.
    /// Mid-season run: N so far, N-1, N-2. Missing seasons are left out (the weights rescale).
    /// </summary>
    public IReadOnlyList<int> HistorySeasons(IReadOnlyList<int> seasonIdsOldestFirst, int targetSeasonId, bool midSeason) =>
        CandidateSeasons(seasonIdsOldestFirst, targetSeasonId, midSeason).Take(pricing.SeasonWeights().Count).ToList();

    /// <summary>Every season that could feed a run, newest first: before N (pre-season), or N and before (mid-season).</summary>
    public static IReadOnlyList<int> CandidateSeasons(IReadOnlyList<int> seasonIdsOldestFirst, int targetSeasonId, bool midSeason)
    {
        var index = seasonIdsOldestFirst.ToList().IndexOf(targetSeasonId);
        if (index < 0) return [];

        var newest = midSeason ? index : index - 1;
        return Enumerable.Range(0, newest + 1).Select(k => seasonIdsOldestFirst[newest - k]).ToList();
    }

    /// <summary>
    /// The newest seasons that actually have lineup and event data, up to the number of season weights.
    /// A season whose matches are in the database without lineups or events is missing data, not a
    /// season nobody played; counting it would give every player 0 minutes, so it's skipped.
    /// </summary>
    public IReadOnlyList<int> SeasonsWithData(IEnumerable<int> candidatesNewestFirst, Func<int, bool> hasData) =>
        candidatesNewestFirst.Where(hasData).Take(pricing.SeasonWeights().Count).ToList();

    /// <summary>12a: once per window unless forced; a pre-season run must happen before the Gameweek 1 deadline.</summary>
    public static RuleCheck CanRecalculate(
        DateTimeOffset? pricesCalculatedAt, bool force, bool preSeason, DateTimeOffset? gameweekOneDeadline, DateTimeOffset now)
    {
        var check = new RuleCheck();
        if (pricesCalculatedAt is not null && !force)
        {
            check.Fail($"Prices for this window were already calculated on {pricesCalculatedAt:yyyy-MM-dd HH:mm} UTC. Add ?force=true to run again.");
        }
        if (preSeason && gameweekOneDeadline is not null && now >= gameweekOneDeadline)
        {
            check.Fail("The pre-season price run must happen before the Gameweek 1 deadline, which has passed.");
        }
        return check;
    }

    /// <summary>
    /// 12i backtest for one position: average real points per price band, and the correlation
    /// between price and real points. Correlation uses double: it's a report statistic, not money.
    /// </summary>
    public static PositionBacktest Backtest(Position position, IReadOnlyList<(decimal Price, decimal RealPoints)> players, decimal bandWidth = 1.0m)
    {
        var bands = players
            .GroupBy(p => Math.Floor(p.Price / bandWidth) * bandWidth)
            .OrderBy(g => g.Key)
            .Select(g => new PriceBand(g.Key, g.Key + bandWidth, g.Count(), Math.Round(g.Average(p => p.RealPoints), 1)))
            .ToList();

        return new PositionBacktest(position, players.Count, bands, Correlation(players));
    }

    public static double? Correlation(IReadOnlyList<(decimal X, decimal Y)> points)
    {
        if (points.Count < 2) return null;
        var xs = points.Select(p => (double)p.X).ToList();
        var ys = points.Select(p => (double)p.Y).ToList();
        var mx = xs.Average();
        var my = ys.Average();
        var cov = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
        var vx = xs.Sum(x => (x - mx) * (x - mx));
        var vy = ys.Sum(y => (y - my) * (y - my));
        return vx == 0 || vy == 0 ? null : Math.Round(cov / Math.Sqrt(vx * vy), 3);
    }
}

public sealed record PriceBand(decimal From, decimal To, int Players, decimal AverageRealPoints);

public sealed record PositionBacktest(Position Position, int Players, IReadOnlyList<PriceBand> Bands, double? PriceVsPointsCorrelation);
