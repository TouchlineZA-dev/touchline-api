namespace Touchline.Api.Models;

// Read-only models for the `football` schema. The sync worker owns these tables;
// the API's database role (app_api_user) can only SELECT them.
// Columns follow touchline-sync db/migrations V003–V032.
//
// ID SPACES: app tables reference Player.Id (internal). match_events and
// player_lineups reference Player.SportmonksPlayerId. Bridge through Player —
// never join an internal id to a Sportmonks id (BACKEND_INTEGRATION.md §7).

public class Competition
{
    public int Id { get; set; }
    public long SportmonksCompetitionId { get; set; }
    public string Name { get; set; } = "";
    public string? ShortCode { get; set; }
    public int? CountryId { get; set; }
    public string? Type { get; set; }
    public string? SubType { get; set; }
    public bool Active { get; set; }
    public string? ImagePath { get; set; }
    public short? Category { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Season
{
    public int Id { get; set; }
    public long SportmonksSeasonId { get; set; }
    public int CompetitionId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly? StartingAt { get; set; }
    public DateOnly? EndingAt { get; set; }
    public bool IsCurrent { get; set; }
    public bool Finished { get; set; }
    public bool Pending { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Team
{
    public int Id { get; set; }
    public long SportmonksTeamId { get; set; }
    public string Name { get; set; } = "";
    public string? ShortCode { get; set; }
    public int? CountryId { get; set; }
    public int? VenueId { get; set; }
    public short? Founded { get; set; }
    public string? Type { get; set; }
    public string? ImagePath { get; set; }
    public bool IsPlaceholder { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Player
{
    public int Id { get; set; }
    public long SportmonksPlayerId { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? DisplayName { get; set; }
    public string? CommonName { get; set; }
    public int? CountryId { get; set; }
    public int? NationalityId { get; set; }
    public short? PositionId { get; set; }
    public short? DetailedPositionId { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public short? Height { get; set; }
    public short? Weight { get; set; }
    public string? ImagePath { get; set; }
    public string? Gender { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Fixture
{
    public int Id { get; set; }
    public long SportmonksFixtureId { get; set; }
    public int CompetitionId { get; set; }
    public int SeasonId { get; set; }
    public int? StageId { get; set; }
    public int? AggregateId { get; set; }
    public int? VenueId { get; set; }
    public short? StateId { get; set; }
    public int? HomeTeamId { get; set; }
    public int? AwayTeamId { get; set; }
    public short? HomeScore { get; set; }
    public short? AwayScore { get; set; }
    public short? HomePenaltyScore { get; set; }
    public short? AwayPenaltyScore { get; set; }
    /// <summary>Kickoff in UTC. Convert to SAST (UTC+2) for display.</summary>
    public DateTimeOffset? StartingAt { get; set; }
    public string? ResultInfo { get; set; }
    public string? Leg { get; set; }
    public short? Length { get; set; }
    public bool IsPlaceholder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class MatchEvent
{
    public int Id { get; set; }
    public long SportmonksEventId { get; set; }
    public int FixtureId { get; set; }
    public int? TeamId { get; set; }
    /// <summary>Sportmonks id — join to Player.SportmonksPlayerId, not Player.Id.</summary>
    public long? SportmonksPlayerId { get; set; }
    public long? SportmonksRelatedPlayerId { get; set; }
    public string? PlayerName { get; set; }
    public string? RelatedPlayerName { get; set; }
    public short TypeId { get; set; }
    public short? Minute { get; set; }
    public short? ExtraMinute { get; set; }
    public string? Result { get; set; }
    public string? Addition { get; set; }
    public short? SortOrder { get; set; }
    public bool? Rescinded { get; set; }
    public bool OnBench { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PlayerLineup
{
    public int Id { get; set; }
    public long SportmonksLineupId { get; set; }
    public int FixtureId { get; set; }
    public int? TeamId { get; set; }
    /// <summary>Sportmonks id — join to Player.SportmonksPlayerId, not Player.Id.</summary>
    public long? SportmonksPlayerId { get; set; }
    public string? PlayerName { get; set; }
    public short? JerseyNumber { get; set; }
    public short? PositionId { get; set; }
    public short TypeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// football.player_match_stats. Despite the table name, each row is a TEAM-level
/// statistic for a fixture (BACKEND_INTEGRATION.md §3, §9).
/// </summary>
public class TeamMatchStat
{
    public int Id { get; set; }
    public long SportmonksStatId { get; set; }
    public int FixtureId { get; set; }
    public int? TeamId { get; set; }
    public short TypeId { get; set; }
    public int? Value { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
