using Microsoft.EntityFrameworkCore;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed record FreshnessReport(bool Fresh, DateTimeOffset? LastFootballCommitUtc, int? AgeMinutes, int StaleAfterHours);

public sealed class HealthService(TouchlineDbContext db, ILogger<HealthService> logger, TimeProvider time)
{
    /// <summary>Hours without any football commit before data counts as stale (the writer runs hourly; PSL data can pause between rounds).</summary>
    public const int StaleAfterHours = 48;

    /// <summary>Readiness: can the API query its database as app_api_user?</summary>
    public async Task<bool> ReadyAsync(CancellationToken ct)
    {
        try
        {
            await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"").SingleAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Readiness check could not query the database");
            return false;
        }
    }

    /// <summary>Data freshness: the newest football commit (fixtures or events), from Postgres only.</summary>
    public async Task<FreshnessReport> FreshnessAsync(CancellationToken ct)
    {
        var fixture = await db.Fixtures.MaxAsync(f => (DateTimeOffset?)f.UpdatedAt, ct);
        var evt = await db.MatchEvents.MaxAsync(e => (DateTimeOffset?)e.UpdatedAt, ct);
        var latest = new[] { fixture, evt }.Where(x => x is not null).Max();
        int? age = latest is null ? null : (int)(time.GetUtcNow() - latest.Value).TotalMinutes;
        return new FreshnessReport(age is not null && age < StaleAfterHours * 60, latest, age, StaleAfterHours);
    }

    public async Task<HealthReport> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var lastJob = await db.SyncJobs
                .OrderByDescending(j => j.StartedAt)
                .Select(j => new LastSyncJob(j.JobType, j.Status, j.StartedAt, j.CompletedAt))
                .FirstOrDefaultAsync(ct);

            return new HealthReport(true, null, lastJob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Details go to the log only; the response must not leak hosts or credentials.
            logger.LogError(ex, "Health check could not query the database");
            return new HealthReport(false, "Database unreachable. See server logs.", null);
        }
    }
}
