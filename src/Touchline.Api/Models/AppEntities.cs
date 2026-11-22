namespace Touchline.Api.Models;

// Models for the `app` schema, which this API owns (read + write).
// Columns follow touchline-sync db/migrations V009, V014–V018, V022–V025, V031.
// No DELETE is granted on `app` — use flags (e.g. IsActive) instead of deleting.
// Schema changes go through Flyway in the sync repo, not ad hoc DDL.

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Required by V014. Proposed V033 makes it nullable (Supabase Auth holds credentials).</summary>
    public string? PasswordHash { get; set; }
    /// <summary>Supabase Auth user id (JWT "sub"). Requires proposed migration V033.</summary>
    public Guid? AuthUserId { get; set; }
    public bool IsActive { get; set; } = true;
    // app.users.is_admin (proposed V034) is deliberately not mapped: inserting users through
    // /me must keep working before V034. AdminService reads it with its own query.
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Gameweek
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Nullable until existing rows are backfilled (V031).</summary>
    public int? SeasonId { get; set; }
    /// <summary>Squad changes lock at this time (UTC). Proposed V034.</summary>
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class FantasyTeam
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SeasonId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Millions of rand. Proposed V034 changes the column default to 100.0.</summary>
    public decimal BudgetRemaining { get; set; } = 300.0m;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// app.fantasy_squads — one row per picked player per team per gameweek.
/// DB enforces (V031): a player once per team per gameweek; at most one captain
/// and one vice-captain per team per gameweek.
/// </summary>
public class FantasySquadPick
{
    public int Id { get; set; }
    public int FantasyTeamId { get; set; }
    /// <summary>Internal football.players.id.</summary>
    public int PlayerId { get; set; }
    public int GameweekId { get; set; }
    public bool IsCaptain { get; set; }
    public bool IsViceCaptain { get; set; }
    public bool IsOnBench { get; set; }
    /// <summary>1–4 for bench players, null for starters. Proposed V034.</summary>
    public short? BenchOrder { get; set; }
    public decimal? PurchasePrice { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PointsResult
{
    public int Id { get; set; }
    public int FantasyTeamId { get; set; }
    public int PlayerId { get; set; }
    public int GameweekId { get; set; }
    public int Points { get; set; }
    /// <summary>JSON with the per-event points math.</summary>
    public string? Breakdown { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class LeaderboardEntry
{
    public int Id { get; set; }
    public int GameweekId { get; set; }
    public int UserId { get; set; }
    public int FantasyTeamId { get; set; }
    public int TotalPoints { get; set; }
    public int? Rank { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PlayerPrice
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int SeasonId { get; set; }
    /// <summary>Millions of rand. Proposed V034 changes the column default to 4.0.</summary>
    public decimal Price { get; set; } = 4.0m;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Append-only price history. Insert a new row per change; never update.</summary>
public class PlayerPriceChange
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int SeasonId { get; set; }
    public int GameweekId { get; set; }
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
