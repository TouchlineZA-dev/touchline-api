using Microsoft.EntityFrameworkCore;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed record FreshnessReport(bool Fresh, DateTimeOffset? LastFootballCommitUtc, int? AgeMinutes, int StaleAfterHours);

public sealed record StalledFixture(int FixtureId, short? StateId, DateTimeOffset? StartingAt, DateTimeOffset? LastObservedAt, int? ObservationAgeMinutes);

/// <param name="Source">"sync.live_fixture_health" (touchline-sync V040) or "fallback" (no observation records yet: commits older than 75 minutes).</param>
public sealed record StalledLiveReport(bool Alert, int Count, string Source, IReadOnlyList<StalledFixture> Fixtures);

public sealed class HealthService(TouchlineDbContext db, ILogger<HealthService> logger, TimeProvider time, Data.SchemaFeatures schema)
{
    internal sealed class StalledRow
    {
        public int FixtureId { get; set; }
        public short? StateId { get; set; }
        public DateTimeOffset? StartingAt { get; set; }
        public DateTimeOffset? LastObservedAt { get; set; }
        public double? AgeSeconds { get; set; }
    }

    /// <summary>
    /// Fixtures that should be live but whose poller has stalled (not observed for 3 minutes, per the sync
    /// owner's sync.live_fixture_health). Before V040: fixtures in a should-be-live state whose rows
    /// haven't been committed for 75 minutes. Any result is an alert for the matchday operator.
    /// </summary>
    public async Task<StalledLiveReport> StalledLiveFixturesAsync(CancellationToken ct)
    {
        if (await schema.HasTableAsync("sync", "live_fixture_health", ct))
        {
            var rows = await db.Database.SqlQuery<StalledRow>($"""
                SELECT fixture_id AS "FixtureId", state_id AS "StateId", starting_at AS "StartingAt", last_observed_at AS "LastObservedAt",
                       extract(epoch FROM observation_age)::float8 AS "AgeSeconds"
                FROM sync.live_fixture_health WHERE poll_health = 'stalled' ORDER BY starting_at
                """).ToListAsync(ct);
            var list = rows.Select(r => new StalledFixture(r.FixtureId, r.StateId, r.StartingAt, r.LastObservedAt,
                r.AgeSeconds is { } s ? (int)(s / 60) : null)).ToList();
            return new StalledLiveReport(list.Count > 0, list.Count, "sync.live_fixture_health", list);
        }

        var now = time.GetUtcNow();
        var states = Business.MatchStateMatrix.ShouldBeLiveStates.ToArray();
        var cutoff = now.AddMinutes(-75);
        var fallback = (await db.Fixtures.Where(f => f.StateId != null && states.Contains(f.StateId.Value) && f.UpdatedAt < cutoff).ToListAsync(ct))
            .Select(f => new StalledFixture(f.Id, f.StateId, f.StartingAt, f.UpdatedAt, (int)(now - f.UpdatedAt).TotalMinutes)).ToList();
        return new StalledLiveReport(fallback.Count > 0, fallback.Count, "fallback", fallback);
    }

    internal sealed class QuotaRow
    {
        public string Entity { get; set; } = "";
        public int Remaining { get; set; }
        public DateTimeOffset ResetsAt { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
    }

    /// <summary>Sportmonks allowance the worker last saw (touchline-sync V040). Empty before V040.</summary>
    public async Task<IReadOnlyList<object>> ProviderQuotaAsync(CancellationToken ct) =>
        await schema.HasTableAsync("sync", "provider_quota", ct)
            ? (await db.Database.SqlQuery<QuotaRow>($"""
                SELECT entity AS "Entity", remaining AS "Remaining", resets_at AS "ResetsAt", observed_at AS "ObservedAt" FROM sync.provider_quota ORDER BY entity
                """).ToListAsync(ct)).Select(q => (object)new { q.Entity, q.Remaining, resetsAtUtc = q.ResetsAt, observedAtUtc = q.ObservedAt }).ToList()
            : [];

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
