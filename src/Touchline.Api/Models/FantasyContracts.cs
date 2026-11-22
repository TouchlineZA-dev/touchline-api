using Touchline.Api.Business;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Models;

// Request bodies and responses for /api/v1/fantasy and /api/v1/admin. Money is in millions (7.5 = R7.5m).

// ---- Requests ---------------------------------------------------------------------------------

public sealed record CreateTeamRequest(string Name);

/// <param name="Gameweek">Optional: the gameweek number the app thinks it's editing. If that gameweek has locked, the change is rejected.</param>
public sealed record SquadRequest(
    string Formation, IReadOnlyList<int> Starters, IReadOnlyList<int> Bench, int? CaptainId, int? ViceCaptainId, int? Gameweek = null)
{
    public SquadSelection ToSelection() => new(Formation, Starters ?? [], Bench ?? [], CaptainId, ViceCaptainId);
}

public sealed record TransfersRequest(IReadOnlyList<int> Out, IReadOnlyList<int> In, int? Gameweek = null);

public sealed record ChipRequest(string Chip, int? Gameweek = null);

public sealed record CreateLeagueRequest(string Name);

public sealed record JoinLeagueRequest(string Code);

public sealed record CreateGameweekRequest(int SeasonId, string Name, DateOnly StartDate, DateOnly EndDate, DateTimeOffset? DeadlineAt = null);

public sealed record CreateTransferWindowRequest(int SeasonId, string Name, DateOnly OpensOn, DateOnly ClosesOn);

public sealed record PriceOverrideRequest(int SeasonId, int? ExpectedMinutes, decimal? Price, string Reason);

// ---- Responses --------------------------------------------------------------------------------

public sealed record TeamView(
    int Id,
    string Name,
    int SeasonId,
    decimal BudgetRemaining,
    decimal? SquadValue,
    GameweekRef? NextGameweek);

public sealed record GameweekRef(int Id, int Number, string Name, DateOnly StartDate, DateOnly EndDate, DateTimeOffset? Deadline, bool Locked);

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
    IReadOnlyList<string> Warnings);

public sealed record TransferResult(
    int Transfers,
    int PointsCost,
    int GameweekPointsCost,
    int? FreeTransfersLeft,
    decimal BudgetRemaining,
    SquadView Squad);

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
    IReadOnlyList<PlayerPointsView> Players);

public sealed record LeagueSummaryView(
    string Id,
    string Name,
    bool IsOverall,
    bool IsOwner,
    string? JoinCode,
    int Members,
    int? MyRank,
    bool Provisional);

public sealed record StandingRowView(
    int Rank, int FantasyTeamId, string TeamName, string Username, int Points, int OverallPoints, bool Provisional);

public sealed record StandingsView(
    string LeagueId, string Name, int? Gameweek, bool Provisional, IReadOnlyList<StandingRowView> Rows);

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
