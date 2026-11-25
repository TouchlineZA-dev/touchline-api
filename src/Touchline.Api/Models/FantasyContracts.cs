using Touchline.Api.Business;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;

namespace Touchline.Api.Models;

// Request bodies and responses for /api/v1/fantasy and /api/v1/admin. Money is in millions (7.5 = R7.5m).

// ---- Requests ---------------------------------------------------------------------------------

public sealed record CreateTeamRequest(string Name);

public sealed record RenameTeamRequest(string Name);

/// <param name="GameweekId">The target gameweek id (required). If that gameweek has locked, nothing is saved (409 gameweek_locked).</param>
/// <param name="Gameweek">Legacy: the target gameweek number, accepted when GameweekId is missing.</param>
public sealed record SquadRequest(
    string Formation, IReadOnlyList<int> Starters, IReadOnlyList<int> Bench, int? CaptainId, int? ViceCaptainId, int? Gameweek = null, int? GameweekId = null)
{
    public SquadSelection ToSelection() => new(Formation, Starters ?? [], Bench ?? [], CaptainId, ViceCaptainId);
}

public sealed record TransfersRequest(IReadOnlyList<int> Out, IReadOnlyList<int> In, int? Gameweek = null, int? GameweekId = null);

public sealed record ChipRequest(string Chip, int? Gameweek = null, int? GameweekId = null);

public sealed record CreateLeagueRequest(string Name);

public sealed record JoinLeagueRequest(string Code);

public sealed record TransferOwnershipRequest(int FantasyTeamId);

public sealed record CreateGameweekRequest(int SeasonId, string Name, DateOnly StartDate, DateOnly EndDate, DateTimeOffset? DeadlineAt = null);

public sealed record CreateTransferWindowRequest(int SeasonId, string Name, DateOnly OpensOn, DateOnly ClosesOn);

public sealed record PriceOverrideRequest(int SeasonId, int? ExpectedMinutes, decimal? Price, string Reason);

/// <param name="SeasonId">Defaults to the current PSL season.</param>
public sealed record AvailabilityOverrideRequest(bool Available, string Reason, int? SeasonId = null);

// ---- Responses --------------------------------------------------------------------------------

public sealed record TeamView(
    int Id,
    string Name,
    int SeasonId,
    decimal BudgetRemaining,
    decimal? SquadValue,
    GameweekRef? NextGameweek);

/// <param name="Deadline">The exact deadline instant (UTC).</param>
/// <param name="DeadlineSast">The same instant in South African time (UTC+02:00), ISO 8601.</param>
public sealed record GameweekRef(int Id, int Number, string Name, DateOnly StartDate, DateOnly EndDate, DateTimeOffset? Deadline, bool Locked)
{
    public string? DeadlineSast => Deadline?.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record SquadPlayerView(
    int PlayerId,
    string Name,
    Position Position,
    int? ClubId,
    string? Club,
    decimal Price,
    decimal? PurchasePrice,
    bool IsStarter,
    int? BenchOrder,
    bool IsCaptain,
    bool IsViceCaptain,
    bool NoClubYet);

public sealed record SquadView(
    int TeamId,
    GameweekRef Gameweek,
    string? Formation,
    int? CaptainId,
    int? ViceCaptainId,
    string? Chip,
    int TransfersMade,
    int TransferCost,
    int? FreeTransfersLeft,
    decimal BudgetRemaining,
    decimal SquadValue,
    int Version,
    bool Saved,
    IReadOnlyList<SquadPlayerView> Players,
    IReadOnlyList<string> Warnings,
    DateTimeOffset? ServerTimeUtc = null);

public sealed record TransferResult(
    int Transfers,
    int PointsCost,
    int GameweekPointsCost,
    int? FreeTransfersLeft,
    decimal BudgetRemaining,
    SquadView Squad,
    DateTimeOffset? ServerTimeUtc = null);

public sealed record FixtureStatusView(
    int FixtureId, string HomeTeam, string AwayTeam, DateTimeOffset? KickoffUtc, MatchStatus Status, short? HomeScore, short? AwayScore);

public sealed record PlayerPointsView(
    int PlayerId,
    string Name,
    Position Position,
    bool IsStarter,
    bool IsCaptain,
    bool IsViceCaptain,
    int Multiplier,
    bool Counted,
    int Points,
    int Contribution,
    PointsBreakdown Breakdown);

public sealed record TeamPointsView(
    int TeamId,
    GameweekRef Gameweek,
    bool Provisional,
    string? Chip,
    int Total,
    int PointsBeforeHits,
    int TransferCost,
    int BenchPoints,
    IReadOnlyList<FixtureStatusView> Fixtures,
    IReadOnlyList<PlayerPointsView> Players,
    string? RulesVersion = null,
    RevisionView? Revision = null);

public sealed record LeagueSummaryView(
    string Id,
    string Name,
    bool IsOverall,
    bool IsOwner,
    string? JoinCode,
    int Members,
    int? MyRank,
    bool Provisional);

/// <param name="PreviousRank">Rank before the current gameweek's points; Movement = PreviousRank − Rank (positive = up).</param>
public sealed record StandingRowView(
    int Rank, int FantasyTeamId, string TeamName, string Username, int Points, int OverallPoints, bool Provisional,
    int? PreviousRank = null, int? Movement = null);

/// <param name="Revision">The published score revisions the totals come from ("r{id}"), plus "+live" while provisional points are included.</param>
public sealed record StandingsView(
    string LeagueId, string Name, int? Gameweek, bool Provisional, IReadOnlyList<StandingRowView> Rows,
    int Page = 1, int PageSize = 0, int TotalRows = 0, string? Revision = null, StandingRowView? Me = null);

public sealed record SnapshotView(
    TeamView Team,
    SquadView? Squad,
    IReadOnlyList<string> ChipsUsed,
    int? FreeTransfersLeft,
    DateTimeOffset? Deadline,
    TeamPointsView? LatestPoints,
    IReadOnlyList<LeagueSummaryView> Leagues,
    DateTimeOffset GeneratedAtUtc);

public sealed record PriceRowView(
    int PlayerId,
    string Name,
    Position Position,
    decimal? ExpectedPoints,
    decimal? Percentile,
    decimal Price,
    decimal? PreviousPrice,
    decimal ExpectedMinutes,
    IReadOnlyList<int> SeasonsUsed,
    bool FallbackPrice,
    bool PriceOverridden,
    bool MinutesOverridden);

public sealed record SeasonDataReport(
    int PslSeasonsInDatabase,
    IReadOnlyList<string> SeasonsWithResults,
    IReadOnlyList<string> HistorySeasonsUsed,
    string? Warning);

public sealed record PricingReport(
    int SeasonId,
    string Run,
    bool Saved,
    int? EffectiveGameweekId,
    int PlayersPriced,
    int PricesChanged,
    int FallbackPrices,
    SeasonDataReport Data,
    IReadOnlyList<PositionBacktest>? Backtest,
    IReadOnlyList<PriceRowView> Players);
