using Touchline.Api.Business;
using Touchline.Api.Services;

namespace Touchline.Api.Models;

// GET /api/v1/fixtures/{id}. Every time is UTC. Player ids are football.players.id; a player the
// provider named but who can't be matched has playerId null and resolved false.

public sealed record IdName(int Id, string Name);

public sealed record PlayerRef(int? PlayerId, string Name, bool Resolved);

/// <param name="Score">Null until the match has started (a scheduled match has an unknown score, not 0–0).</param>
public sealed record MatchTeamView(int? TeamId, string Name, string? ShortCode, string? CrestUrl, int? Score);

public sealed record PenaltyScoreView(short? Home, short? Away);

public sealed record MatchFixtureView(
    int Id,
    long SportmonksFixtureId,
    IdName? Competition,
    IdName? Season,
    DateTimeOffset? KickoffUtc,
    MatchStatus Status,
    MatchPhase Phase,
    short? StateId,
    string StateCode,
    bool NeedsReview,
    MatchTeamView Home,
    MatchTeamView Away,
    PenaltyScoreView? Penalties,
    int LengthMinutes);

public sealed record ClockView(int? Minute, string? Display, ClockSource Source, bool Running);

public sealed record FixtureGameweekView(int Id, int Number, AssignmentSource AssignmentSource);

/// <param name="RelatedRole">What RelatedPlayer is: "assist" for goals, "player_off" for substitutions (Player is the one coming on).</param>
public sealed record MatchEventView(
    int Id,
    long SportmonksEventId,
    string Type,
    short TypeId,
    short? Minute,
    short? ExtraMinute,
    string Display,
    int? TeamId,
    string? Side,
    PlayerRef? Player,
    PlayerRef? RelatedPlayer,
    string? RelatedRole,
    string? Result,
    string? Addition);

public sealed record EventPageView(int Page, int PageSize, int Total, IReadOnlyList<MatchEventView> Items);

public sealed record LineupPlayerView(PlayerRef Player, short? JerseyNumber, Position Position);

public sealed record LineupTeamView(int? TeamId, IReadOnlyList<LineupPlayerView> StartingXi, IReadOnlyList<LineupPlayerView> Bench);

public sealed record LineupsView(LineupTeamView Home, LineupTeamView Away);

public sealed record SubstitutionView(int? TeamId, string? Side, short? Minute, short? ExtraMinute, string Display, PlayerRef? PlayerOn, PlayerRef? PlayerOff);

public sealed record TeamStatView(int? TeamId, string? Side, short TypeId, string? Name, int? Value);

public sealed record UnattributedEventView(int EventId, string Type, string Display, int? TeamId);

/// <param name="Lineups">available | pending (not started or live) | unavailable (finished, never arrived) | not_applicable.</param>
/// <param name="Events">available | none_recorded (finished, no events stored) | pending | not_applicable.</param>
/// <param name="TeamStats">available | pending | unavailable | not_applicable.</param>
/// <param name="PlayerStats">Always "unsupported": the provider plan gives no per-player stats.</param>
/// <param name="UnattributedEvents">Goals, cards and substitutions whose player can't be identified. They count for the team, not for any player.</param>
public sealed record CoverageView(
    string Lineups,
    string Events,
    string TeamStats,
    string PlayerStats,
    int UnattributedEvents,
    IReadOnlyList<UnattributedEventView> Unattributed,
    IReadOnlyList<string> Notes);

/// <param name="ProviderTimestampUtc">Always null: the provider's own update time isn't stored.</param>
/// <param name="FirstObservedUtc">Earliest created_at of the fixture and its events: when the worker first wrote it.</param>
/// <param name="DbCommittedUtc">Latest updated_at of the fixture, events, lineups and stats: the last worker commit.</param>
/// <param name="ApiPublishedUtc">When this response was built.</param>
public sealed record TimestampsView(DateTimeOffset? ProviderTimestampUtc, DateTimeOffset FirstObservedUtc, DateTimeOffset DbCommittedUtc, DateTimeOffset ApiPublishedUtc);

public sealed record FreshnessView(
    DateTimeOffset LastUpdatedUtc,
    DateTimeOffset ServerTimeUtc,
    bool Stale,
    bool InMatchWindow,
    int StaleAfterMinutes,
    int AgeMinutes,
    TimestampsView? Timestamps,
    string Source = "api",
    string? PollHealth = null,
    DateTimeOffset? LastObservedUtc = null);

/// <param name="Revision">The worker's committed revision ("s{n}" from sync.fixture_freshness) when available, otherwise the API's content hash ("r-…").</param>
/// <param name="RevisionSource">"sync.fixture_freshness" or "api_content_hash".</param>
public sealed record MatchDetailView(
    MatchFixtureView Fixture,
    ClockView Clock,
    FixtureGameweekView? Gameweek,
    EventPageView Events,
    LineupsView Lineups,
    IReadOnlyList<SubstitutionView> Substitutions,
    IReadOnlyList<TeamStatView> Stats,
    CoverageView Coverage,
    FreshnessView Freshness,
    string Revision,
    string RevisionSource = "api_content_hash");
