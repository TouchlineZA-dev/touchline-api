using Microsoft.EntityFrameworkCore;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed class HealthService(TouchlineDbContext db, ILogger<HealthService> logger)
{
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
