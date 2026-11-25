using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Services;

public sealed record PlayerMatchView(int FixtureId, DateTimeOffset? KickoffUtc, int? ClubId, string? Club, int? OpponentId, string? Opponent,
    bool Home, int Minutes, int Points, IReadOnlyList<string> Reasons);

public sealed record UpcomingFixtureView(int FixtureId, DateTimeOffset? KickoffUtc, int? OpponentId, string? Opponent, bool Home, int? GameweekNumber);

public sealed record PlayerGameweekPoints(int GameweekId, int Number, int Points, int Minutes, IReadOnlyList<string> Reasons, bool Provisional);

public sealed record PlayerDetailView(
    int Id, long SportmonksPlayerId, string Name, Position Position, int? ClubId, string? Club, string? ImageUrl, decimal Price,
    bool Available, string AvailabilitySource, IReadOnlyList<PlayerMatchView> RecentMatches, IReadOnlyList<UpcomingFixtureView> UpcomingFixtures,
    IReadOnlyList<PlayerGameweekPoints> PointsHistory, DateTimeOffset ServerTimeUtc);

public sealed record ClubDetailView(ClubView Club, StandingRow? Standing, IReadOnlyList<FixtureSummary> Fixtures, IReadOnlyList<PlayerSummary> Squad, DateTimeOffset ServerTimeUtc);

public sealed record RankingRow(int Rank, int PlayerId, long SportmonksPlayerId, string Player, short? JerseyNumber, string? Club, int? ClubId, int Total);

/// <summary>
/// Player detail, club detail and player rankings (03-Leruo-API Stage 6, handbook Gate 4), from
/// Postgres only. Players are identified by id, never by name, so duplicate names stay distinct; each
/// past match keeps the club the player lined up for, so a club move changes future eligibility and
/// upcoming fixtures but not history.
/// </summary>
public sealed class FootballDetailService(
    TouchlineDbContext db,
    FootballService football,
    AvailabilityService availability,
    MatchFactsService facts,
    GameweekService gameweeks,
    StandingsService standings,
    SchemaFeatures schema,
    FootballReads reads,
    IOptions<FantasyRules> rules)
{
    private readonly PlayerScoring _scoring = new(rules.Value);

    public async Task<PlayerDetailView?> PlayerAsync(int playerId, SeasonSummary season, DateTimeOffset now, CancellationToken ct)
    {
        var player = await db.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player is null) return null;

        var position = PositionExtensions.FromSportmonks(player.PositionId);
        var clubs = await football.GetLatestClubsAsync(season.Id, ct);
        int? clubId = clubs.TryGetValue(player.SportmonksPlayerId, out var c) ? c : null;
        var teamNames = await db.Teams.ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var price = await db.PlayerPrices.Where(p => p.PlayerId == playerId && p.SeasonId == season.Id).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct)
            ?? football.DefaultPrice(position);
        var available = await availability.GetAsync(season.Id, ct);
        var lineupRows = await reads.LineupsAsync(ct);
        var inLineups = await lineupRows.AnyAsync(l => l.SportmonksPlayerId == player.SportmonksPlayerId && db.Fixtures.Any(f => f.Id == l.FixtureId && f.SeasonId == season.Id), ct);
        var hasOverride = await schema.HasTableAsync("app", "player_availability_overrides", ct)
            && await db.AvailabilityOverrides.AnyAsync(o => o.PlayerId == playerId && o.SeasonId == season.Id, ct);

        // Every season fixture he lined up in or appears in an event for, with the club he played for then.
        var appearances = await lineupRows
            .Where(l => l.SportmonksPlayerId == player.SportmonksPlayerId)
            .Join(db.Fixtures.Where(f => f.SeasonId == season.Id), l => l.FixtureId, f => f.Id, (l, f) => new { f.Id, l.TeamId })
            .ToListAsync(ct);
        var fixtureIds = appearances.Select(a => a.Id).Distinct().ToList();
        var fixtures = await db.Fixtures.Where(f => fixtureIds.Contains(f.Id)).ToListAsync(ct);
        var playerFacts = (await facts.LoadAsync(fixtures, now, ct)).Where(x => x.PlayerId == playerId).ToDictionary(x => x.Facts.FixtureId);

        PlayerMatchView Match(Fixture f)
        {
            var team = appearances.FirstOrDefault(a => a.Id == f.Id)?.TeamId;
            var home = team == f.HomeTeamId;
            var opponent = home ? f.AwayTeamId : f.HomeTeamId;
            var score = playerFacts.TryGetValue(f.Id, out var pf) ? _scoring.ScoreFixture(pf.Facts, position) : PointsBreakdown.None;
            return new PlayerMatchView(f.Id, f.StartingAt, team, team is { } t ? teamNames.GetValueOrDefault(t) : null, opponent,
                opponent is { } o ? teamNames.GetValueOrDefault(o) : null, home, score.Minutes, score.Total, Reasons(score));
        }

        var recent = fixtures.Where(f => MatchFactsDecoder.Counts(f.StateId) && f.StartingAt <= now)
            .OrderByDescending(f => f.StartingAt).Take(5).Select(Match).ToList();

        var calendar = await gameweeks.GetCalendarAsync(season.Id, ct);
        var upcoming = clubId is null ? [] : (await db.Fixtures
                .Where(f => f.SeasonId == season.Id && (f.HomeTeamId == clubId || f.AwayTeamId == clubId) && f.StartingAt > now)
                .OrderBy(f => f.StartingAt).Take(5).ToListAsync(ct))
            .Where(f => !MatchStateMatrix.For(f.StateId).Settled)
            .Select(f =>
            {
                var home = f.HomeTeamId == clubId;
                var opponent = home ? f.AwayTeamId : f.HomeTeamId;
                return new UpcomingFixtureView(f.Id, f.StartingAt, opponent, opponent is { } o ? teamNames.GetValueOrDefault(o) : null, home, calendar.ForFixture(f.Id)?.Number);
            }).ToList();

        var history = calendar.Gameweeks
            .Where(g => g.IsLocked(now))
            .Select(g =>
            {
                var weekFacts = g.Fixtures.Where(f => playerFacts.ContainsKey(f.Id)).Select(f => playerFacts[f.Id].Facts).ToList();
                var total = _scoring.ScoreGameweek(weekFacts, position);
                return new PlayerGameweekPoints(g.Id, g.Number, total.Total, total.Minutes, Reasons(total), total.Provisional);
            })
            .Where(h => h.Minutes > 0 || h.Points != 0)
            .ToList();

        return new PlayerDetailView(player.Id, player.SportmonksPlayerId, FootballService.NameOf(player), position, clubId,
            clubId is { } id ? teamNames.GetValueOrDefault(id) : null, FootballService.ImageOrNull(player.ImagePath), price,
            position != Position.Unknown && available.IsAvailable(player.Id, player.SportmonksPlayerId),
            hasOverride ? "admin_override" : inLineups ? "current_season_lineup" : "none", recent, upcoming, history, now);
    }

    public async Task<ClubDetailView?> ClubAsync(int clubId, SeasonSummary season, DateTimeOffset now, CancellationToken ct)
    {
        var club = (await football.GetClubsAsync(season.Id, ct)).FirstOrDefault(c => c.Id == clubId);
        if (club is null) return null;
        var table = await standings.GetAsync(season, now, ct);
        var fixtures = await football.GetFixturesAsync(season.Id, null, null, ct, clubId);
        var squad = (await football.GetPlayersAsync(season.Id, ct)).Where(p => p.TeamId == clubId).OrderBy(p => p.Position).ThenBy(p => p.Name).ToList();
        return new ClubDetailView(club, table.Standings.FirstOrDefault(r => r.TeamId == clubId), fixtures, squad, now);
    }

    /// <summary>
    /// GET /player-rankings: the Netlify function's behaviour (goals, assists, clean sheets; top 10 with a total above 0,
    /// ties by name), from final fixtures with both teams and a score. Rescinded events are excluded.
    /// </summary>
    public async Task<object?> RankingsAsync(int? competitionId, int? seasonId, CancellationToken ct)
    {
        var competitions = await db.Competitions.OrderBy(c => c.Name).ToListAsync(ct);
        var competition = competitions.FirstOrDefault(c => competitionId is { } id ? c.SportmonksCompetitionId == id || c.Id == id : c.SportmonksCompetitionId == Codes.PslCompetitionId)
            ?? competitions.FirstOrDefault(c => c.Active) ?? competitions.FirstOrDefault();
        if (competition is null) return null;
        var seasons = await db.Seasons.Where(s => s.CompetitionId == competition.Id).OrderByDescending(s => s.StartingAt).ToListAsync(ct);
        var season = seasons.FirstOrDefault(s => seasonId is { } id ? s.Id == id || s.SportmonksSeasonId == id : s.IsCurrent) ?? seasons.FirstOrDefault();
        if (season is null) return null;

        var fixtures = (await db.Fixtures.Where(f => f.CompetitionId == competition.Id && f.SeasonId == season.Id).ToListAsync(ct))
            .Where(f => MatchStatusExtensions.IsFinal(f.StateId) && f.HomeTeamId is not null && f.AwayTeamId is not null && f.HomeScore is not null && f.AwayScore is not null)
            .ToDictionary(f => f.Id);
        var ids = fixtures.Keys.ToList();
        var events = MatchFactsDecoder.DistinctEvents(await (await reads.EventsAsync(ct)).Where(e => ids.Contains(e.FixtureId)).ToListAsync(ct)).Where(e => e.Rescinded != true).ToList();
        var lineups = await (await reads.LineupsAsync(ct)).Where(l => ids.Contains(l.FixtureId)).ToListAsync(ct);
        var smIds = events.SelectMany(e => new[] { e.SportmonksPlayerId, e.SportmonksRelatedPlayerId }).Concat(lineups.Select(l => l.SportmonksPlayerId))
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var players = await db.Players.Where(p => smIds.Contains(p.SportmonksPlayerId)).ToDictionaryAsync(p => p.SportmonksPlayerId, ct);
        var teams = await db.Teams.ToDictionaryAsync(t => t.Id, ct);
        var latest = lineups.Where(l => l.SportmonksPlayerId is not null)
            .GroupBy(l => l.SportmonksPlayerId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => fixtures[l.FixtureId].StartingAt).First());

        var goals = new Dictionary<long, int>();
        var assists = new Dictionary<long, int>();
        foreach (var e in events.Where(e => e.TypeId is Codes.EventTypes.Goal or Codes.EventTypes.PenaltyScored))
        {
            if (e.SportmonksPlayerId is { } scorer) goals[scorer] = goals.GetValueOrDefault(scorer) + 1;
            if (e.SportmonksRelatedPlayerId is { } a && a != e.SportmonksPlayerId) assists[a] = assists.GetValueOrDefault(a) + 1;
        }
        var cleanSheets = new Dictionary<long, int>();
        foreach (var l in lineups.Where(l => l.TypeId == (Codes.LineupTypes.StartingXi ?? 11) && l.PositionId is Codes.PositionIds.Goalkeeper or Codes.PositionIds.Defender && l.SportmonksPlayerId is not null)
                     .DistinctBy(l => (l.FixtureId, l.SportmonksPlayerId)))
        {
            var f = fixtures[l.FixtureId];
            var conceded = l.TeamId == f.HomeTeamId ? f.AwayScore : l.TeamId == f.AwayTeamId ? f.HomeScore : null;
            if (conceded == 0) cleanSheets[l.SportmonksPlayerId!.Value] = cleanSheets.GetValueOrDefault(l.SportmonksPlayerId!.Value) + 1;
        }

        IReadOnlyList<RankingRow> Rank(Dictionary<long, int> totals) => totals
            .Where(kv => kv.Value > 0 && players.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var p = players[kv.Key];
                var line = latest.GetValueOrDefault(kv.Key);
                var team = line?.TeamId is { } t ? teams.GetValueOrDefault(t) : null;
                return (Player: p, Line: line, Team: team, Total: kv.Value, Name: FootballService.NameOf(p));
            })
            .OrderByDescending(x => x.Total).ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .Take(10)
            .Select((x, i) => new RankingRow(i + 1, x.Player.Id, x.Player.SportmonksPlayerId, x.Name, x.Line?.JerseyNumber, x.Team?.Name, x.Team?.Id, x.Total))
            .ToList();

        return new
        {
            competition = new { competition.Id, sportmonksCompetitionId = competition.SportmonksCompetitionId, competition.Name, competition.ShortCode },
            competitions = competitions.Select(x => new { x.Id, sportmonksCompetitionId = x.SportmonksCompetitionId, x.Name, x.ShortCode, x.Active }),
            season = new { season.Id, sportmonksSeasonId = season.SportmonksSeasonId, season.Name, isCurrent = season.IsCurrent },
            seasons = seasons.Select(x => new { x.Id, sportmonksSeasonId = x.SportmonksSeasonId, x.Name, isCurrent = x.IsCurrent }),
            matchesCounted = fixtures.Count,
            rankings = new { goals = Rank(goals), assists = Rank(assists), cleanSheets = Rank(cleanSheets) },
        };
    }

    private static IReadOnlyList<string> Reasons(PointsBreakdown b)
    {
        var list = new List<string>();
        if (b.Appearance != 0) list.Add($"Appearance ({b.Minutes} min): {b.Appearance:+0;-0}");
        if (b.GoalPoints != 0) list.Add($"{b.Goals} goal(s): {b.GoalPoints:+0;-0}");
        if (b.AssistPoints != 0) list.Add($"{b.Assists} assist(s): {b.AssistPoints:+0;-0}");
        if (b.CleanSheetPoints != 0) list.Add($"Clean sheet: {b.CleanSheetPoints:+0;-0}");
        if (b.YellowPoints != 0) list.Add($"Yellow card(s): {b.YellowPoints:+0;-0}");
        if (b.RedPoints != 0) list.Add($"Red card: {b.RedPoints:+0;-0}");
        if (b.OwnGoalPoints != 0) list.Add($"Own goal(s): {b.OwnGoalPoints:+0;-0}");
        return list;
    }
}
