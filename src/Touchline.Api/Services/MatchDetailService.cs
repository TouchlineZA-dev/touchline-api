using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Services;

/// <summary>Freshness settings ("Freshness" section). Times in minutes.</summary>
public sealed class FreshnessOptions
{
    public const string SectionName = "Freshness";

    /// <summary>
    /// During a match window, data older than this is stale. The writer (Netlify sync-fixtures) runs
    /// hourly, so the default allows one run plus a margin.
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 75;

    /// <summary>The match window opens this long before kickoff (lineups arrive about 30 minutes before).</summary>
    public int WindowBeforeKickoffMinutes { get; set; } = 120;

    /// <summary>The match window closes this long after kickoff (match plus the worker's 4 h settle time).</summary>
    public int WindowAfterKickoffMinutes { get; set; } = 360;
}

/// <summary>
/// GET /api/v1/fixtures/{id}: everything about one match, read from Postgres only. Status, phase and
/// clock come from MatchStateMatrix and MatchClock (the same ones scoring uses); player ids are bridged
/// by PlayerIdBridge (the same one scoring uses).
/// </summary>
public sealed class MatchDetailService(
    TouchlineDbContext db,
    PlayerIdBridge bridge,
    GameweekService gameweeks,
    FootballReads reads,
    SchemaFeatures schema,
    IOptions<FantasyRules> rules,
    IOptions<FreshnessOptions> freshness)
{
    public const int DefaultEventsPageSize = 200;
    public const int MaxEventsPageSize = 500;

    /// <summary>Team-level stat names for the codes BACKEND_INTEGRATION.md §6 lists. Others come back with a null name.</summary>
    private static readonly IReadOnlyDictionary<short, string> StatNames = new Dictionary<short, string>
    {
        [34] = "Corners", [42] = "Shots total", [86] = "Shots on target", [45] = "Ball possession %", [52] = "Goals",
        [56] = "Fouls", [84] = "Yellow cards", [83] = "Red cards", [57] = "Saves", [79] = "Assists", [51] = "Offsides",
    };

    private readonly MatchClock _clock = new(rules.Value);

    public async Task<MatchDetailView?> GetAsync(int fixtureId, int eventsPage, int eventsPageSize, DateTimeOffset now, CancellationToken ct)
    {
        var fixture = await db.Fixtures.FirstOrDefaultAsync(f => f.Id == fixtureId, ct);
        if (fixture is null) return null;

        // Only these columns of player_match_stats are read: its player_id exists in production but is always NULL (never read it).
        var stats = await db.TeamMatchStats.Where(s => s.FixtureId == fixtureId)
            .Select(s => new { s.TeamId, s.TypeId, s.Value, s.Location, s.UpdatedAt, s.CreatedAt })
            .ToListAsync(ct);
        var lineups = await (await reads.LineupsAsync(ct)).Where(l => l.FixtureId == fixtureId).ToListAsync(ct);
        var allEvents = MatchFactsDecoder.DistinctEvents(await (await reads.EventsAsync(ct)).Where(e => e.FixtureId == fixtureId).ToListAsync(ct)).ToList();
        var events = allEvents.Where(e => e.Rescinded != true).ToList();

        var teamIds = new[] { fixture.HomeTeamId, fixture.AwayTeamId }.Where(t => t is not null).Select(t => t!.Value).ToList();
        var teams = await db.Teams.Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var competition = await db.Competitions.Where(c => c.Id == fixture.CompetitionId).Select(c => new { c.Id, c.Name }).FirstOrDefaultAsync(ct);
        var season = await db.Seasons.Where(s => s.Id == fixture.SeasonId).Select(s => new { s.Id, s.Name }).FirstOrDefaultAsync(ct);
        var players = await bridge.ResolveAsync(PlayerIdBridge.IdsIn(lineups, events), ct);

        var state = MatchStateMatrix.For(fixture.StateId);
        var latestMinute = events.Where(e => e.Minute is not null).Select(e => (int?)e.Minute).Max();
        var clock = _clock.Read(fixture.StateId, fixture.StartingAt, fixture.Length, latestMinute, now);

        PlayerRef? Ref(long? sportmonksId, string? providerName) =>
            sportmonksId is null && string.IsNullOrWhiteSpace(providerName)
                ? null
                : sportmonksId is { } id && players.TryGetValue(id, out var p)
                    ? new PlayerRef(p.Id, p.Name, Resolved: true)
                    : new PlayerRef(null, string.IsNullOrWhiteSpace(providerName) ? "Unknown player" : providerName, Resolved: false);

        var ordered = events
            .OrderBy(e => e.Minute ?? 0).ThenBy(e => e.ExtraMinute ?? 0).ThenBy(e => e.SortOrder ?? 0).ThenBy(e => e.SportmonksEventId)
            .ToList();
        var comingOnIsPlayerId = Codes.Substitutions.PlayerIdIsComingOn ?? true;

        var eventViews = ordered.Select(e =>
        {
            var (type, relatedRole) = EventType(e.TypeId);
            var player = Ref(e.SportmonksPlayerId, e.PlayerName);
            var related = Ref(e.SportmonksRelatedPlayerId, e.RelatedPlayerName);
            if (e.TypeId == Codes.EventTypes.Substitution && !comingOnIsPlayerId) (player, related) = (related, player);
            return new MatchEventView(
                e.Id, e.SportmonksEventId, type, e.TypeId, e.Minute, e.ExtraMinute, MatchClock.Display(e.Minute, e.ExtraMinute),
                e.TeamId, Side(fixture, e.TeamId), player, related, relatedRole, e.Result, e.Addition);
        }).ToList();

        var size = Math.Clamp(eventsPageSize <= 0 ? DefaultEventsPageSize : eventsPageSize, 1, MaxEventsPageSize);
        var page = Math.Max(1, eventsPage);
        var pagedEvents = new EventPageView(page, size, eventViews.Count, eventViews.Skip((page - 1) * size).Take(size).ToList());

        var startingXi = Codes.LineupTypes.StartingXi ?? 11;
        LineupTeamView LineupFor(int? teamId) => new(
            teamId,
            lineups.Where(l => l.TeamId == teamId && l.TypeId == startingXi).OrderBy(l => l.JerseyNumber ?? 999).Select(LineupRow).ToList(),
            lineups.Where(l => l.TeamId == teamId && l.TypeId != startingXi).OrderBy(l => l.JerseyNumber ?? 999).Select(LineupRow).ToList());
        LineupPlayerView LineupRow(PlayerLineup l) => new(
            Ref(l.SportmonksPlayerId, l.PlayerName) ?? new PlayerRef(null, "Unknown player", false),
            l.JerseyNumber, PositionExtensions.FromSportmonks(l.PositionId));

        var substitutions = eventViews.Where(e => e.TypeId == Codes.EventTypes.Substitution)
            .Select(e => new SubstitutionView(e.TeamId, e.Side, e.Minute, e.ExtraMinute, e.Display, e.Player, e.RelatedPlayer))
            .ToList();

        var statViews = stats.OrderBy(s => s.TypeId).ThenBy(s => s.Location)
            .Select(s => new TeamStatView(s.TeamId, s.Location ?? Side(fixture, s.TeamId), s.TypeId, StatNames.GetValueOrDefault(s.TypeId), s.Value))
            .ToList();

        // Unattributed: a scoring or disciplinary event whose player can't be identified. It still counts
        // for the team (the score, and so clean sheets), but nobody gets the player points.
        var unattributed = events
            .Where(e => e.TypeId is Codes.EventTypes.Goal or Codes.EventTypes.PenaltyScored or Codes.EventTypes.OwnGoal
                or Codes.EventTypes.YellowCard or Codes.EventTypes.RedCard or Codes.EventTypes.SecondYellowRed or Codes.EventTypes.Substitution)
            .Where(e =>
            {
                var id = e.TypeId == Codes.EventTypes.Substitution && !comingOnIsPlayerId ? e.SportmonksRelatedPlayerId : e.SportmonksPlayerId;
                return id is null || !players.ContainsKey(id.Value);
            })
            .Select(e => new UnattributedEventView(e.Id, EventType(e.TypeId).Type, MatchClock.Display(e.Minute, e.ExtraMinute), e.TeamId))
            .ToList();

        var coverage = new CoverageView(
            Lineups: lineups.Count > 0 ? "available" : Waiting(state, fixture.StartingAt, now),
            Events: events.Count > 0 ? "available" : state.Played && state.ResultFinal ? "none_recorded" : Waiting(state, fixture.StartingAt, now),
            TeamStats: stats.Count > 0 ? "available" : Waiting(state, fixture.StartingAt, now),
            PlayerStats: "unsupported",
            UnattributedEvents: unattributed.Count,
            Unattributed: unattributed,
            Notes:
            [
                "Team stats are team-level only and zero values are often omitted: a missing stat is unknown, not zero.",
                "'none_recorded' means no events are stored for a finished match; a genuine quiet match and missing data look the same until the worker records completeness.",
            ]);

        var timestamps = new[] { fixture.UpdatedAt }
            .Concat(allEvents.Select(e => e.UpdatedAt)).Concat(lineups.Select(l => l.UpdatedAt)).Concat(stats.Select(s => s.UpdatedAt))
            .ToList();
        var lastCommitted = timestamps.Max();
        var firstObserved = new[] { fixture.CreatedAt }.Concat(allEvents.Select(e => e.CreatedAt)).Min();
        var sync = await SyncFreshnessAsync(fixture.Id, ct);
        var fresh = sync is null ? Freshness(fixture, state, lastCommitted, now) : SyncFreshness(fixture, state, sync, now);

        var calendar = await gameweeks.GetCalendarAsync(fixture.SeasonId, ct);
        var gameweek = calendar.ForFixture(fixture.Id);

        var showScore = state.Played || state.ResultFinal;
        return new MatchDetailView(
            new MatchFixtureView(
                fixture.Id, fixture.SportmonksFixtureId,
                competition is null ? null : new IdName(competition.Id, competition.Name),
                season is null ? null : new IdName(season.Id, season.Name),
                fixture.StartingAt, state.Status, state.Phase, fixture.StateId, state.Code, state.NeedsReview,
                TeamSide(fixture.HomeTeamId, teams, showScore ? fixture.HomeScore ?? 0 : null),
                TeamSide(fixture.AwayTeamId, teams, showScore ? fixture.AwayScore ?? 0 : null),
                fixture.HomePenaltyScore is null && fixture.AwayPenaltyScore is null ? null : new PenaltyScoreView(fixture.HomePenaltyScore, fixture.AwayPenaltyScore),
                fixture.Length ?? rules.Value.DefaultMatchLengthMinutes),
            new ClockView(clock.Minute, clock.Minute?.ToString(System.Globalization.CultureInfo.InvariantCulture), clock.Source, clock.Running),
            gameweek is null ? null : new FixtureGameweekView(gameweek.Id, gameweek.Number,
                calendar.AssignmentSources?.GetValueOrDefault(fixture.Id) ?? AssignmentSource.DateWindow),
            pagedEvents,
            new LineupsView(LineupFor(fixture.HomeTeamId), LineupFor(fixture.AwayTeamId)),
            substitutions,
            statViews,
            coverage,
            fresh with
            {
                Timestamps = new TimestampsView(
                    ProviderTimestampUtc: sync?.LastProviderAt,
                    FirstObservedUtc: firstObserved,
                    DbCommittedUtc: sync?.LastChangedAt ?? lastCommitted,
                    ApiPublishedUtc: now),
            },
            sync is null ? Revision(fixture, allEvents, lineups) : $"s{sync.Revision}",
            sync is null ? "api_content_hash" : "sync.fixture_freshness");
    }

    /// <summary>
    /// Event-set revision: a hash of the facts that change what the match shows or scores (state, score,
    /// every stored event's provider id, type, players, minute and rescinded flag, and the lineup).
    /// Timestamps are left out, so ingesting the same snapshot again gives the same revision; any
    /// correction (a re-attributed scorer, a rescinded or removed event) gives a new one.
    /// Format: "r-" + 16 hex characters.
    /// </summary>
    public static string Revision(Fixture fixture, IEnumerable<MatchEvent> events, IEnumerable<PlayerLineup> lineups)
    {
        var sb = new StringBuilder();
        sb.Append($"f:{fixture.Id}|{fixture.StateId}|{fixture.HomeScore}|{fixture.AwayScore}|{fixture.HomePenaltyScore}|{fixture.AwayPenaltyScore}\n");
        foreach (var e in MatchFactsDecoder.DistinctEvents(events).OrderBy(e => e.SportmonksEventId).ThenBy(e => e.Id))
        {
            sb.Append($"e:{e.SportmonksEventId}|{e.TypeId}|{e.SportmonksPlayerId}|{e.SportmonksRelatedPlayerId}|{e.TeamId}|{e.Minute}|{e.ExtraMinute}|{e.Rescinded == true}\n");
        }
        foreach (var l in lineups.OrderBy(l => l.SportmonksLineupId))
        {
            sb.Append($"l:{l.SportmonksLineupId}|{l.SportmonksPlayerId}|{l.TeamId}|{l.TypeId}\n");
        }
        return "r-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Stale when the match is inside its window (kickoff − before … kickoff + after) and nothing was
    /// committed for StaleAfterMinutes, or when the window has closed and the fixture still isn't
    /// settled (the worker never recorded how it ended).
    /// </summary>
    public FreshnessView Freshness(Fixture fixture, MatchState state, DateTimeOffset lastCommitted, DateTimeOffset now)
    {
        var o = freshness.Value;
        var age = now - lastCommitted;
        var inWindow = false;
        var stale = false;
        if (fixture.StartingAt is { } kickoff)
        {
            var opens = kickoff.AddMinutes(-o.WindowBeforeKickoffMinutes);
            var closes = kickoff.AddMinutes(o.WindowAfterKickoffMinutes);
            inWindow = now >= opens && now <= closes;
            stale = (inWindow && age > TimeSpan.FromMinutes(o.StaleAfterMinutes) && !state.Settled)
                    || (now > closes && !state.Settled && lastCommitted < closes)
                    || (MatchStateMatrix.ShouldBeLive(fixture.StateId) && age > TimeSpan.FromMinutes(o.StaleAfterMinutes));
        }
        return new FreshnessView(lastCommitted, now, stale, inWindow, o.StaleAfterMinutes, (int)Math.Max(0, age.TotalMinutes), null);
    }

    internal sealed class SyncFreshnessRow
    {
        public int Revision { get; set; }
        public DateTimeOffset LastObservedAt { get; set; }
        public DateTimeOffset LastChangedAt { get; set; }
        public DateTimeOffset? LastProviderAt { get; set; }
        public string? PollHealth { get; set; }
    }

    /// <summary>
    /// The worker's own record of a fixture (touchline-sync V040): its committed revision, when it was
    /// last observed and changed, and for fixtures that should be live, sync.live_fixture_health's
    /// poll_health. Null before V040 is applied or before the worker first records the fixture.
    /// </summary>
    private async Task<SyncFreshnessRow?> SyncFreshnessAsync(int fixtureId, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("sync", "fixture_freshness", ct)) return null;
        var health = await schema.HasTableAsync("sync", "live_fixture_health", ct);
        return health
            ? await db.Database.SqlQuery<SyncFreshnessRow>($"""
                SELECT ff.revision AS "Revision", ff.last_observed_at AS "LastObservedAt", ff.last_changed_at AS "LastChangedAt",
                       ff.last_provider_at AS "LastProviderAt", h.poll_health AS "PollHealth"
                FROM sync.fixture_freshness ff
                LEFT JOIN sync.live_fixture_health h ON h.fixture_id = ff.fixture_id
                WHERE ff.fixture_id = {fixtureId}
                """).FirstOrDefaultAsync(ct)
            : await db.Database.SqlQuery<SyncFreshnessRow>($"""
                SELECT revision AS "Revision", last_observed_at AS "LastObservedAt", last_changed_at AS "LastChangedAt",
                       last_provider_at AS "LastProviderAt", NULL::text AS "PollHealth"
                FROM sync.fixture_freshness WHERE fixture_id = {fixtureId}
                """).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Freshness from the worker's observations. For a fixture that should be live (the sync owner's state
    /// list), stale means the poller stalled (poll_health = 'stalled': not observed for 3 minutes); a quiet
    /// match that is still being observed is not stale. Otherwise the window rule applies, measured from
    /// the last observation rather than the last change.
    /// </summary>
    private FreshnessView SyncFreshness(Fixture fixture, MatchState state, SyncFreshnessRow sync, DateTimeOffset now)
    {
        var window = Freshness(fixture, state, sync.LastObservedAt, now);
        var stale = MatchStateMatrix.ShouldBeLive(fixture.StateId) && sync.PollHealth is not null
            ? sync.PollHealth == "stalled"
            : window.Stale;
        return window with
        {
            LastUpdatedUtc = sync.LastChangedAt,
            Stale = stale,
            Source = "sync.fixture_freshness",
            PollHealth = sync.PollHealth,
            LastObservedUtc = sync.LastObservedAt,
            AgeMinutes = (int)Math.Max(0, (now - sync.LastObservedAt).TotalMinutes),
        };
    }

    private static string Waiting(MatchState state, DateTimeOffset? kickoff, DateTimeOffset now)
    {
        if (state.Settled && !state.Played) return "not_applicable";   // postponed, cancelled, abandoned, awarded
        if (state.Played && state.ResultFinal) return "unavailable";      // finished and never arrived
        return "pending";                                                // not started yet, or live
    }

    private static string? Side(Fixture f, int? teamId) =>
        teamId is null ? null : teamId == f.HomeTeamId ? "home" : teamId == f.AwayTeamId ? "away" : null;

    private static MatchTeamView TeamSide(int? teamId, IReadOnlyDictionary<int, Team> teams, int? score)
    {
        var team = teamId is { } id ? teams.GetValueOrDefault(id) : null;
        return new MatchTeamView(teamId, team?.Name ?? "TBD", team?.ShortCode, FootballService.ImageOrNull(team?.ImagePath), score);
    }

    /// <summary>Event type name and what the related player means (BACKEND_INTEGRATION.md §6, Part 1b codes).</summary>
    public static (string Type, string? RelatedRole) EventType(short typeId) => typeId switch
    {
        Codes.EventTypes.Goal => ("goal", "assist"),
        Codes.EventTypes.PenaltyScored => ("penalty_goal", "assist"),
        Codes.EventTypes.OwnGoal => ("own_goal", null),
        Codes.EventTypes.PenaltyMissed => ("penalty_missed", null),
        Codes.EventTypes.Substitution => ("substitution", "player_off"),
        Codes.EventTypes.YellowCard => ("yellow_card", null),
        Codes.EventTypes.RedCard => ("red_card", null),
        Codes.EventTypes.SecondYellowRed => ("second_yellow", null),
        Codes.EventTypes.Var => ("var", null),
        _ => ("other", null),
    };
}
