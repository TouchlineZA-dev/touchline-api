namespace Touchline.Api.Models;

// Read-only models for the `sync` schema (owned by the sync worker).
// Columns follow touchline-sync db/migrations V010–V012, V029, V031, V032.

public class SyncJob
{
    public int Id { get; set; }
    public string JobType { get; set; } = "";
    /// <summary>running | success | failed | unknown (V032 check constraint).</summary>
    public string Status { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class SyncLog
{
    public int Id { get; set; }
    public int SyncJobId { get; set; }
    public string Message { get; set; } = "";
    public string Level { get; set; } = "";
    public DateTimeOffset LoggedAt { get; set; }
}

/// <summary>
/// The "is it safe to score?" gate, one row per gameweek (unique since V031).
/// Note: per BACKEND_INTEGRATION.md §4 the sync worker does not populate this
/// yet; until it does, gate scoring on each fixture's state being final.
/// </summary>
public class GameweekSyncStatus
{
    public int Id { get; set; }
    public int GameweekId { get; set; }
    public bool FixturesComplete { get; set; }
    public bool EventsComplete { get; set; }
    public bool StatsComplete { get; set; }
    public bool MarkedComplete { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
