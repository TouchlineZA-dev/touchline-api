namespace Touchline.Api.Models;

// Models for the app tables added by proposed migration V034 (docs/proposed-migrations).
// Until V034 is applied, any query touching these fails with "undefined table/column" and the
// fantasy endpoints return 503 "Schema not ready".

/// <summary>app.fantasy_gameweek_entries: one row per team per gameweek.</summary>
public class FantasyGameweekEntry
{
    public int Id { get; set; }
    public int FantasyTeamId { get; set; }
    public int GameweekId { get; set; }
    public string Formation { get; set; } = "";
    /// <summary>bench_boost, triple_captain, wildcard or null.</summary>
    public string? Chip { get; set; }
    public int TransfersMade { get; set; }
    /// <summary>Points deducted for extra transfers this gameweek (a positive number).</summary>
    public int TransferCost { get; set; }
    /// <summary>Final team points after hits. Null until the gameweek is finalised.</summary>
    public int? Points { get; set; }
    public int? BenchPoints { get; set; }
    public DateTimeOffset? ScoredAt { get; set; }
    /// <summary>Incremented on every change; the If-Match version.</summary>
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.fantasy_transfers: insert only.</summary>
public class FantasyTransfer
{
    public int Id { get; set; }
    public int FantasyTeamId { get; set; }
    public int GameweekId { get; set; }
    public int PlayerOutId { get; set; }
    public int PlayerInId { get; set; }
    public decimal PriceOut { get; set; }
    public decimal PriceIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>app.fantasy_chip_usage: each chip once per season, one chip per gameweek.</summary>
public class FantasyChipUsage
{
    public int Id { get; set; }
    public int FantasyTeamId { get; set; }
    public int SeasonId { get; set; }
    public string Chip { get; set; } = "";
    public int GameweekId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>app.leagues: private, joined by code.</summary>
public class League
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string JoinCode { get; set; } = "";
    public int OwnerUserId { get; set; }
    public int SeasonId { get; set; }
    public short MaxMembers { get; set; } = 20;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.league_members. Leaving sets LeftAt; there's no DELETE.</summary>
public class LeagueMember
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public int FantasyTeamId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

/// <summary>app.transfer_windows: an admin enters the official PSL dates.</summary>
public class TransferWindow
{
    public int Id { get; set; }
    public int SeasonId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly OpensOn { get; set; }
    public DateOnly ClosesOn { get; set; }
    public DateTimeOffset? PricesCalculatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.player_price_overrides: insert only, the latest per player and season wins.</summary>
public class PlayerPriceOverride
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int SeasonId { get; set; }
    public int? ExpectedMinutes { get; set; }
    public decimal? Price { get; set; }
    public string Reason { get; set; } = "";
    public int CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
