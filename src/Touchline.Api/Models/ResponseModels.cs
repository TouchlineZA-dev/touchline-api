using Touchline.Api.Business;

namespace Touchline.Api.Models;

// Response shapes returned by the services. Controllers return these as JSON (camelCase).

public record SeasonSummary(int Id, string Name, int CompetitionId, DateOnly? StartingAt, DateOnly? EndingAt);

public record TeamSummary(int Id, string Name, string? ShortCode, string? CrestUrl);

/// <param name="Id">football.teams.id: the stable club id used everywhere in the API (favourite club, fixtures).</param>
public record ClubView(int Id, long SportmonksTeamId, string Name, string? ShortCode, string? Crest);

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
    decimal Price,
    bool Available);

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
    short? AwayScore,
    MatchPhase Phase = MatchPhase.Scheduled,
    bool NeedsReview = false,
    int? CompetitionId = null)
{
    /// <summary>Deprecated alias of KickoffUtc for the web client during the rollout (docs/api/web-migration.md). Same instant, UTC.</summary>
    public DateTimeOffset? Kickoff => KickoffUtc;
}

public record GameweekSummary(
    int Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsActive,
    int? SeasonId,
    bool ReadyToScore,
    int? Number = null,
    DateTimeOffset? Deadline = null)
{
    /// <summary>The same deadline in South African time (UTC+02:00), ISO 8601.</summary>
    public string? DeadlineSast => Deadline?.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}

public record HealthReport(bool DatabaseReachable, string? DatabaseError, LastSyncJob? LastSyncJob);

public record LastSyncJob(string JobType, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
