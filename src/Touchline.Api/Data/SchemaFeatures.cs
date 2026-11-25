using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Touchline.Api.Data;

/// <summary>
/// Which optional tables and columns exist in the connected database. New migrations are written
/// here but applied by hand later (SPRINT_RULES.md rule 4), so code that uses them checks first and
/// falls back to the old behaviour instead of failing. Answers are cached per process for a few
/// minutes, so a migration applied while the API runs is picked up without a restart.
/// </summary>
public sealed class SchemaFeatures(IServiceScopeFactory scopes, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (bool Exists, DateTimeOffset CheckedAt)> _cache = new();

    public Task<bool> HasTableAsync(string schema, string table, CancellationToken ct = default) =>
        CheckAsync($"t:{schema}.{table}", db => db.Database
            .SqlQuery<bool>($"SELECT to_regclass({schema + "." + table}) IS NOT NULL AS \"Value\"")
            .SingleAsync(ct));

    public Task<bool> HasColumnAsync(string schema, string table, string column, CancellationToken ct = default) =>
        CheckAsync($"c:{schema}.{table}.{column}", db => db.Database
            .SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = {schema} AND table_name = {table} AND column_name = {column}) AS "Value"
                """)
            .SingleAsync(ct));

    /// <summary>The column exists and accepts NULL.</summary>
    public Task<bool> IsNullableAsync(string schema, string table, string column, CancellationToken ct = default) =>
        CheckAsync($"n:{schema}.{table}.{column}", db => db.Database
            .SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = {schema} AND table_name = {table} AND column_name = {column}
                                 AND is_nullable = 'YES') AS "Value"
                """)
            .SingleAsync(ct));

    /// <summary>Forget cached answers (tests apply migrations mid-run).</summary>
    public void Reset() => _cache.Clear();

    private async Task<bool> CheckAsync(string key, Func<TouchlineDbContext, Task<bool>> query)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && now - cached.CheckedAt < CacheFor) return cached.Exists;

        // Own scope and connection: a failed check must never abort the caller's transaction.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
        var exists = await query(db);
        _cache[key] = (exists, now);
        return exists;
    }
}
