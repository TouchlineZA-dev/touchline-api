using Touchline.Api.Business;

namespace Touchline.Api.Models;

// Response shapes returned by the services. Controllers return these as JSON (camelCase).

public record SeasonSummary(int Id, string Name, int CompetitionId, DateOnly? StartingAt, DateOnly? EndingAt);

public record TeamSummary(int Id, string Name, string? ShortCode, string? CrestUrl);

public record PlayerSummary(
    int Id,
    long SportmonksPlayerId,
    string Name,
    Position Position,
    int? TeamId,
    string? Team,
    string? TeamCrest,
    int? NationalityId,
    string? ImageUrl,
    decimal Price);

public record FixtureSummary(
    int Id,
    long SportmonksFixtureId,
    DateTimeOffset? KickoffUtc,
    MatchStatus Status,
    short? StateId,
    int? HomeTeamId,
    string HomeTeam,
    int? AwayTeamId,
    string AwayTeam,
    short? HomeScore,
    short? AwayScore);

public record GameweekSummary(
    int Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive,
    int? SeasonId,
    bool ReadyToScore);

public record HealthReport(bool DatabaseReachable, string? DatabaseError, LastSyncJob? LastSyncJob);

public record LastSyncJob(string JobType, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
