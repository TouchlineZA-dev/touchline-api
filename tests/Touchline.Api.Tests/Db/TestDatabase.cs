using System.Text.RegularExpressions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Touchline.Api.Tests.Db;

/// <summary>A disposable database: an admin connection for seeding and the API's restricted one.</summary>
/// <param name="AdminConnectionString">Superuser. Seeds football/sync rows the API can't write.</param>
/// <param name="ApiConnectionString">app_api_user, with exactly the grants and RLS the migrations give it.</param>
public sealed record TestDb(string Name, string AdminConnectionString, string ApiConnectionString);

/// <summary>
/// Builds throwaway PostgreSQL databases from the CANONICAL schema: touchline-sync's Flyway
/// migrations (pinned copy in tests/db/flyway, see tests/db/flyway/PIN.md, or a live checkout via
/// TOUCHLINE_FLYWAY_DIR), then this repo's pending migrations (docs/migrations 007 and later, the
/// ones not yet in the pinned set), then tests/db/supplement (objects that exist in production but
/// not in any migration, such as football.standings).
///
/// Server: TOUCHLINE_TEST_PG (an admin connection string to any PostgreSQL 15+ server, e.g. a local
/// portable install), otherwise one Testcontainers container (Docker) shared by the whole test run.
/// Without either, tests skip locally; CI sets TOUCHLINE_REQUIRE_DB_TESTS=true so they fail instead.
/// Never point TOUCHLINE_TEST_PG at a shared or production database: databases are created on it.
/// </summary>
public static class TestDatabase
{
    /// <summary>docs/migrations 001–006 are V033–V038, already in the pinned Flyway set; 007+ are V043+ (V039–V042 belong to the sync owner).</summary>
    public const int FirstPendingApiMigration = 7;
    public const int ApiMigrationVersionOffset = 36;

    /// <summary>Password the test sets for app_api_user (the migrations' own role passwords are never used).</summary>
    private const string ApiRolePassword = "api-test-only";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>Role and grant changes are cluster-wide catalog updates; parallel test classes create databases one at a time.</summary>
    private static readonly SemaphoreSlim CreateGate = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static string? _server;
    private static string? _unavailable;

    public static bool Required => Environment.GetEnvironmentVariable("TOUCHLINE_REQUIRE_DB_TESTS") == "true";

    /// <summary>Admin connection string for the server, or null (with a reason) when none is available.</summary>
    public static async Task<(string? Server, string? SkipReason)> ServerAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_server is not null || _unavailable is not null) return (_server, _unavailable);

            var configured = Environment.GetEnvironmentVariable("TOUCHLINE_TEST_PG");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                _server = configured;
                return (_server, null);
            }
            try
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await _container.StartAsync();
                _server = _container.GetConnectionString();
            }
            catch (Exception ex) when (!Required)
            {
                _unavailable = $"No test PostgreSQL (set TOUCHLINE_TEST_PG or run Docker): {ex.GetBaseException().Message}";
            }
            return (_server, _unavailable);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Creates a database with Flyway migrations up to <paramref name="upToVersion"/> (inclusive) of the
    /// combined list, then <paramref name="extraScripts"/>. upToVersion null = everything, including
    /// pending API migrations and the supplement.
    /// </summary>
    public static async Task<TestDb> CreateAsync(string server, string prefix, int? upToVersion = null, params string[] extraScripts)
    {
        await CreateGate.WaitAsync();
        try
        {
            return await CreateCoreAsync(server, prefix, upToVersion, extraScripts);
        }
        finally
        {
            CreateGate.Release();
        }
    }

    private static async Task<TestDb> CreateCoreAsync(string server, string prefix, int? upToVersion, string[] extraScripts)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 33, 63)];
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await Exec(admin, $"CREATE DATABASE {name}");
        }

        var adminCs = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false }.ToString();
        await using (var conn = new NpgsqlConnection(adminCs))
        {
            await conn.OpenAsync();
            foreach (var (version, sql) in Migrations().Where(m => upToVersion is null || m.Version <= upToVersion))
            {
                try
                {
                    await Exec(conn, version == 1 ? GuardRoleCreation(sql) : sql);
                }
                catch (PostgresException ex)
                {
                    throw new InvalidOperationException($"Migration V{version:000} failed on the test database: {ex.MessageText}", ex);
                }
            }
            if (upToVersion is null)
            {
                var supplement = Path.Combine(RepoRoot(), "tests", "db", "supplement");
                foreach (var file in (Directory.Exists(supplement) ? Directory.GetFiles(supplement, "*.sql") : []).Order())
                {
                    await Exec(conn, await File.ReadAllTextAsync(file));
                }
            }
            foreach (var script in extraScripts) await Exec(conn, script);
            await Exec(conn, $"ALTER ROLE app_api_user WITH LOGIN PASSWORD '{ApiRolePassword}'");
        }

        var apiCs = new NpgsqlConnectionStringBuilder(server)
        {
            Database = name,
            Username = "app_api_user",
            Password = ApiRolePassword,
        }.ToString();
        return new TestDb(name, adminCs, apiCs);
    }

    /// <summary>Runs SQL on the admin connection (seeding football/sync rows, simulating the worker).</summary>
    public static async Task ExecAsync(this TestDb db, string sql, params object?[] args)
    {
        await using var conn = new NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args) cmd.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(this TestDb db, string sql, params object?[] args)
    {
        await using var conn = new NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args) cmd.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The combined, ordered migration list: pinned Flyway V001… then docs/migrations 007+ as V043+.
    /// </summary>
    public static IReadOnlyList<(int Version, string Sql)> Migrations()
    {
        var flywayDir = Environment.GetEnvironmentVariable("TOUCHLINE_FLYWAY_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(RepoRoot(), "tests", "db", "flyway");

        var list = Directory.GetFiles(flywayDir, "V*__*.sql")
            .Select(f => (Version: int.Parse(Regex.Match(Path.GetFileName(f), @"^V(\d+)__").Groups[1].Value), File: f))
            .ToList();
        var pinnedMax = list.Max(m => m.Version);

        var pending = Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "migrations"), "*.sql")
            .Select(f => (Match: Regex.Match(Path.GetFileName(f), @"^(\d{3})_"), File: f))
            .Where(x => x.Match.Success)
            .Select(x => (Version: int.Parse(x.Match.Groups[1].Value) + ApiMigrationVersionOffset, x.File))
            .Where(x => x.Version - ApiMigrationVersionOffset >= FirstPendingApiMigration && x.Version > pinnedMax);

        return list.Concat(pending)
            .OrderBy(m => m.Version)
            .Select(m => (m.Version, File.ReadAllText(m.File)))
            .ToList();
    }

    /// <summary>
    /// V001 creates the service roles (cluster-wide) with passwords. Several test databases share one
    /// server, so creation is made conditional; the passwords in V001 are replaced by the test's own.
    /// </summary>
    private static string GuardRoleCreation(string sql) =>
        // V001 had "CREATE USER x WITH PASSWORD '…';" until touchline-sync removed the passwords (24 Nov 2026): "CREATE USER x;".
        Regex.Replace(sql, @"CREATE USER (\w+)(?: WITH PASSWORD '[^']*')?;", m =>
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{m.Groups[1].Value}') THEN " +
            $"CREATE ROLE {m.Groups[1].Value} LOGIN PASSWORD '{ApiRolePassword}'; END IF; END $$;");

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Touchline.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("Could not find Touchline.sln above " + AppContext.BaseDirectory);
    }
}
