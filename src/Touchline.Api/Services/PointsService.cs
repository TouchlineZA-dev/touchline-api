using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>Every player's points for one gameweek, worked out from the latest football data.</summary>
public sealed record GameweekPlayerPoints(IReadOnlyDictionary<int, PointsBreakdown> ByPlayer, bool Provisional)
{
    public PointsBreakdown For(int playerId) => ByPlayer.TryGetValue(playerId, out var b) ? b : PointsBreakdown.None;
}

/// <summary>A team's points for a gameweek and the season so far.</summary>
public sealed record TeamTotals(int GameweekPoints, int OverallPoints, bool Provisional);

/// <summary>
/// Live (provisional) and final points. Provisional points are never saved: they're worked out on
/// request and cached in memory for 60 seconds. Final points are saved when an admin finalises a
/// gameweek, which is only allowed once the sync worker marks it complete or every fixture is final.
/// </summary>
public sealed class PointsService(
    TouchlineDbContext db,
    MatchFactsService facts,
    SquadStore squads,
    GameweekService gameweeks,
    IMemoryCache cache,
    IOptions<FantasyRules> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FantasyRules _rules = options.Value;
    private readonly PlayerScoring _playerScoring = new(options.Value);
    private readonly TeamScoring _teamScoring = new(options.Value);

    /// <summary>All players' points for a gameweek from the latest data. Cached for Fantasy:LivePointsCacheSeconds.</summary>
    public async Task<GameweekPlayerPoints> PlayerPointsAsync(GameweekInfo gameweek, DateTimeOffset now, bool useCache, CancellationToken ct)
    {
        var key = $"fantasy:gw-points:{gameweek.Id}";
        if (useCache && cache.TryGetValue(key, out GameweekPlayerPoints? cached) && cached is not null) return cached;

        var all = await facts.LoadAsync(gameweek.Fixtures, now, ct);
        var byPlayer = all
            .GroupBy(f => f.PlayerId)
            .ToDictionary(g => g.Key, g => _playerScoring.ScoreGameweek(g.Select(x => x.Facts), g.First().Position));

        var provisional = gameweek.Fixtures.Count == 0 || gameweek.Fixtures.Any(f => !MatchStatusExtensions.IsFinal(f.StateId));
        var points = new GameweekPlayerPoints(byPlayer, provisional);
        cache.Set(key, points, TimeSpan.FromSeconds(_rules.LivePointsCacheSeconds));
        return points;
    }

    /// <summary>GET /fantasy/team/points: final if the gameweek has been finalised for this team, otherwise provisional.</summary>
    public async Task<TeamPointsView> TeamPointsAsync(
        FantasyTeam team, GameweekInfo gameweek, SeasonCalendar calendar, DateTimeOffset now, CancellationToken ct)
    {
        var squad = await squads.LoadAsync(team.Id, gameweek, calendar, ct)
            ?? throw new NotFoundException("Your team has no squad for that gameweek yet.");

        var final = squad.Saved && squad.Entry.ScoredAt is not null;
        Dictionary<int, PointsBreakdown> breakdowns;
        if (final)
        {
            var rows = await db.PointsResults.Where(r => r.FantasyTeamId == team.Id && r.GameweekId == gameweek.Id).ToListAsync(ct);
            var saved = rows.GroupBy(r => r.PlayerId).ToDictionary(g => g.Key, g => ReadBreakdown(g.First().Breakdown, g.First().Points));
            breakdowns = squad.Picks.ToDictionary(p => p.PlayerId, p => saved.GetValueOrDefault(p.PlayerId, PointsBreakdown.None));
        }
        else
        {
            var live = await PlayerPointsAsync(gameweek, now, useCache: true, ct);
            breakdowns = squad.Picks.ToDictionary(p => p.PlayerId, p => live.For(p.PlayerId));
        }

        var score = ScoreTeam(squad, breakdowns);
        var players = await db.Players.Where(p => breakdowns.Keys.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        return new TeamPointsView(
            team.Id,
            gameweek.ToRef(now),
            Provisional: !final,
            squad.Entry.Chip,
            score.Total,
            score.PointsBeforeHits,
            score.TransferCost,
            score.BenchPoints,
            await FixtureStatusesAsync(gameweek, ct),
            squad.Picks.Select(pick =>
            {
                var s = score.Picks.Single(x => x.PlayerId == pick.PlayerId);
                var player = players.GetValueOrDefault(pick.PlayerId);
                return new PlayerPointsView(
                    pick.PlayerId,
                    player is null ? $"Player {pick.PlayerId}" : FootballService.NameOf(player),
                    PositionExtensions.FromSportmonks(player?.PositionId),
                    !pick.IsOnBench,
                    pick.IsCaptain,
                    pick.IsViceCaptain,
                    s.Multiplier,
                    s.Counted,
                    s.BasePoints,
                    s.Contribution,
                    breakdowns[pick.PlayerId]);
            }).OrderBy(p => p.IsStarter ? 0 : 1).ThenBy(p => p.Position).ToList());
    }

    /// <summary>
    /// Points for many teams at once, for standings. Overall = finalised gameweeks plus, while the
    /// current gameweek isn't finalised, its provisional points. <paramref name="gameweek"/> picks the
    /// single-gameweek column (defaults to the current gameweek).
    /// </summary>
    public async Task<Dictionary<int, TeamTotals>> TeamTotalsAsync(
        IReadOnlyCollection<int> teamIds, SeasonCalendar calendar, GameweekInfo? gameweek, DateTimeOffset now, CancellationToken ct)
    {
        var current = calendar.Current(now);
        gameweek ??= current;
        var gameweekIds = calendar.Gameweeks.Select(g => g.Id).ToList();

        var entries = await db.GameweekEntries
            .Where(e => teamIds.Contains(e.FantasyTeamId) && gameweekIds.Contains(e.GameweekId))
            .ToListAsync(ct);
        var scored = entries.Where(e => e.ScoredAt is not null && e.Points is not null).ToList();

        // Provisional points for gameweeks that have locked but aren't finalised yet: the current one,
        // and the selected one if different.
        var provisionalWeeks = new[] { current, gameweek }.Where(g => g is not null && g.IsLocked(now)).Distinct().ToList();
        var provisional = new Dictionary<(int Team, int Gw), int>();
        foreach (var week in provisionalWeeks)
        {
            var unscored = teamIds.Where(t => !scored.Any(e => e.FantasyTeamId == t && e.GameweekId == week!.Id)).ToList();
            if (unscored.Count == 0) continue;

            GameweekPlayerPoints live;
            try
            {
                live = await PlayerPointsAsync(week!, now, useCache: true, ct);
            }
            catch (SportmonksCodesNotConfirmedException)
            {
                break; // Standings still work with finalised points only until the codes are confirmed.
            }
            foreach (var (teamId, squad) in await LoadSquadsAsync(unscored, week!, calendar, ct))
            {
                var breakdowns = squad.Picks.ToDictionary(p => p.PlayerId, p => live.For(p.PlayerId));
                provisional[(teamId, week!.Id)] = ScoreTeam(squad, breakdowns).Total;
            }
        }

        return teamIds.ToDictionary(teamId => teamId, teamId =>
        {
            var finalTotal = scored.Where(e => e.FantasyTeamId == teamId).Sum(e => e.Points!.Value);
            var currentProvisional = current is not null && provisional.TryGetValue((teamId, current.Id), out var cp) ? cp : 0;

            int weekPoints = 0;
            var weekProvisional = false;
            if (gameweek is not null)
            {
                var entry = scored.FirstOrDefault(e => e.FantasyTeamId == teamId && e.GameweekId == gameweek.Id);
                if (entry is not null) weekPoints = entry.Points!.Value;
                else if (provisional.TryGetValue((teamId, gameweek.Id), out var wp)) { weekPoints = wp; weekProvisional = true; }
            }

            return new TeamTotals(weekPoints, finalTotal + currentProvisional, weekProvisional || currentProvisional != 0);
        });
    }

    /// <summary>
    /// POST /admin/gameweeks/{id}/score. Saves every team's final points. Safe to run again: rows are
    /// updated, never duplicated.
    /// </summary>
    public async Task<object> FinaliseAsync(int gameweekId, DateTimeOffset now, CancellationToken ct)
    {
        var gameweek = await db.Gameweeks.FirstOrDefaultAsync(g => g.Id == gameweekId, ct)
            ?? throw new NotFoundException($"Gameweek {gameweekId} doesn't exist.");
        if (gameweek.SeasonId is not { } seasonId)
        {
            throw new RuleViolationException("This gameweek has no season_id, so its fixtures can't be found. Backfill it first (V031).");
        }

        var calendar = await gameweeks.GetCalendarAsync(seasonId, ct);
        var info = calendar.ById(gameweekId)!;
        var markedComplete = await gameweeks.IsMarkedCompleteAsync(gameweekId, ct);
        if (!GameweekRules.CanFinalise(markedComplete, info.Fixtures.Select(f => f.StateId).ToList()))
        {
            throw new ConflictException(
                "Not every fixture in this gameweek is final (state 5, 7 or 8), and the sync worker hasn't marked it complete.",
                "Gameweek not finished");
        }

        var live = await PlayerPointsAsync(info, now, useCache: false, ct);
        var teamIds = await db.FantasyTeams.Where(t => t.SeasonId == seasonId).Select(t => t.Id).ToListAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var scoredTeams = 0;
        foreach (var teamId in teamIds)
        {
            var squad = await squads.EnsureAsync(teamId, info, calendar, now, ct);
            if (squad is null) continue; // never picked a squad

            var breakdowns = squad.Picks.ToDictionary(p => p.PlayerId, p => live.For(p.PlayerId));
            var score = ScoreTeam(squad, breakdowns);

            var existing = await db.PointsResults.AsTracking()
                .Where(r => r.FantasyTeamId == teamId && r.GameweekId == gameweekId)
                .ToDictionaryAsync(r => r.PlayerId, ct);
            foreach (var pick in score.Picks)
            {
                if (!existing.TryGetValue(pick.PlayerId, out var row))
                {
                    row = new PointsResult { FantasyTeamId = teamId, PlayerId = pick.PlayerId, GameweekId = gameweekId };
                    db.PointsResults.Add(row);
                }
                row.Points = pick.BasePoints;
                row.Breakdown = WriteBreakdown(breakdowns[pick.PlayerId], pick);
            }

            squad.Entry.Points = score.Total;
            squad.Entry.BenchPoints = score.BenchPoints;
            squad.Entry.ScoredAt = now;
            await db.SaveChangesAsync(ct);
            scoredTeams++;
        }
        await transaction.CommitAsync(ct);
        cache.Remove($"fantasy:gw-points:{gameweekId}");

        return new { gameweekId, gameweek = info.Number, teamsScored = scoredTeams, markedComplete, finalisedAtUtc = now };
    }

    public async Task<IReadOnlyList<FixtureStatusView>> FixtureStatusesAsync(GameweekInfo gameweek, CancellationToken ct)
    {
        var teamIds = gameweek.Fixtures.SelectMany(f => new[] { f.HomeTeamId, f.AwayTeamId }).Where(t => t is not null).Select(t => t!.Value).Distinct().ToList();
        var names = await db.Teams.Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        string NameOf(int? id) => id is not null && names.TryGetValue(id.Value, out var n) ? n : "TBD";

        return gameweek.Fixtures.Select(f => new FixtureStatusView(
            f.Id, NameOf(f.HomeTeamId), NameOf(f.AwayTeamId), f.StartingAt, MatchStatusExtensions.FromStateId(f.StateId), f.HomeScore, f.AwayScore)).ToList();
    }

    private TeamGameweekScore ScoreTeam(LoadedSquad squad, IReadOnlyDictionary<int, PointsBreakdown> breakdowns) =>
        _teamScoring.Score(
            squad.Picks.Select(p => new TeamPickPoints(p.PlayerId, !p.IsOnBench, p.IsCaptain, p.IsViceCaptain,
                breakdowns.TryGetValue(p.PlayerId, out var b) ? b : PointsBreakdown.None)).ToList(),
            squad.Entry.Chip,
            squad.Entry.TransferCost);

    /// <summary>Squads for many teams in one go: saved picks for the gameweek, or each team's latest earlier picks.</summary>
    private async Task<Dictionary<int, LoadedSquad>> LoadSquadsAsync(
        IReadOnlyCollection<int> teamIds, GameweekInfo gameweek, SeasonCalendar calendar, CancellationToken ct)
    {
        var upTo = calendar.Gameweeks.Where(g => g.Number <= gameweek.Number).ToDictionary(g => g.Id, g => g.Number);
        var gwIds = upTo.Keys.ToList();
        var picks = await db.FantasySquads.Where(p => teamIds.Contains(p.FantasyTeamId) && gwIds.Contains(p.GameweekId)).ToListAsync(ct);
        var entries = await db.GameweekEntries.Where(e => teamIds.Contains(e.FantasyTeamId) && gwIds.Contains(e.GameweekId)).ToListAsync(ct);

        var result = new Dictionary<int, LoadedSquad>();
        foreach (var teamPicks in picks.GroupBy(p => p.FantasyTeamId))
        {
            var latestGw = teamPicks.Select(p => p.GameweekId).MaxBy(id => upTo[id]);
            var weekPicks = teamPicks.Where(p => p.GameweekId == latestGw).ToList();
            var source = entries.FirstOrDefault(e => e.FantasyTeamId == teamPicks.Key && e.GameweekId == latestGw);
            var saved = latestGw == gameweek.Id;

            var entry = saved && source is not null
                ? source
                : SquadStore.NewEntry(teamPicks.Key, gameweek.Id, source?.Formation ?? "");
            result[teamPicks.Key] = new LoadedSquad(entry, weekPicks, saved);
        }
        return result;
    }

    private static string WriteBreakdown(PointsBreakdown breakdown, PickScore pick)
    {
        var node = JsonSerializer.SerializeToNode(breakdown, Json)!.AsObject();
        node["multiplier"] = pick.Multiplier;
        node["counted"] = pick.Counted;
        node["contribution"] = pick.Contribution;
        return node.ToJsonString();
    }

    private static PointsBreakdown ReadBreakdown(string? json, int points) =>
        string.IsNullOrWhiteSpace(json)
            ? new PointsBreakdown { Total = points }
            : JsonSerializer.Deserialize<PointsBreakdown>(json, Json) ?? new PointsBreakdown { Total = points };
}
