using Npgsql;
using Testcontainers.PostgreSql;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// A throwaway PostgreSQL container (Docker) with three isolated databases, all built from the
/// Flyway DDL for app.users (touchline-sync V002, V014, V015):
///   ready       - V014 + the proposed V033 from docs/proposed-migrations (read from the repo)
///   pre_v033    - V014 only (the schema the sync repo's Flyway history defines today)
///   partial     - V014 + auth_user_id, but password_hash still NOT NULL
/// Never touches a shared or production database.
///
/// Without Docker the tests are skipped locally. CI sets TOUCHLINE_REQUIRE_DB_TESTS=true so a
/// missing database fails the build instead.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Mirrors touchline-sync db/migrations V002 (set_updated_at), V014 (app.users) and V015
    // (app.fantasy_teams). football.seasons is reduced to its key; only the FK matters here.
    private const string FlywayBaseline = """
        CREATE SCHEMA football;
        CREATE SCHEMA app;

        CREATE OR REPLACE FUNCTION set_updated_at()
        RETURNS TRIGGER AS $$
        BEGIN
            NEW.updated_at = NOW();
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        CREATE TABLE football.seasons (id SERIAL PRIMARY KEY);

        CREATE TABLE app.users (
            id              SERIAL PRIMARY KEY,
            email           VARCHAR(255) NOT NULL UNIQUE,
            username        VARCHAR(50) NOT NULL UNIQUE,
            password_hash   VARCHAR(255) NOT NULL,
            is_active       BOOLEAN NOT NULL DEFAULT TRUE,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TRIGGER trg_users_updated_at
        BEFORE UPDATE ON app.users
        FOR EACH ROW EXECUTE FUNCTION set_updated_at();

        CREATE TABLE app.fantasy_teams (
            id              SERIAL PRIMARY KEY,
            user_id         INTEGER NOT NULL REFERENCES app.users(id),
            season_id       INTEGER NOT NULL REFERENCES football.seasons(id),
            name            VARCHAR(100) NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UNIQUE(user_id, season_id)
        );
        """;

    private const string PartialV033 = """
        ALTER TABLE app.users ADD COLUMN auth_user_id UUID;
        ALTER TABLE app.users ADD CONSTRAINT uq_users_auth_user_id UNIQUE (auth_user_id);
        """;

    private PostgreSqlContainer? _container;

    public string? SkipReason { get; private set; }
    public TestTokens Tokens { get; } = new();

    public string ReadyConnectionString { get; private set; } = "";
    public TestApiFactory ReadyApi { get; private set; } = null!;
    public TestApiFactory PreV033Api { get; private set; } = null!;
    public TestApiFactory PartialV033Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("TOUCHLINE_REQUIRE_DB_TESTS") != "true")
        {
            SkipReason = $"PostgreSQL test container unavailable (is Docker running?): {ex.GetBaseException().Message}";
            return;
        }

        var admin = _container.GetConnectionString();
        var v033 = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "docs", "proposed-migrations",
            "V033__app_users_supabase_auth_link.sql"));

        ReadyConnectionString = await CreateDatabaseAsync(admin, "touchline_ready", FlywayBaseline, v033);
        ReadyApi = new TestApiFactory(ReadyConnectionString, Tokens);
        PreV033Api = new TestApiFactory(await CreateDatabaseAsync(admin, "touchline_pre_v033", FlywayBaseline), Tokens);
        PartialV033Api = new TestApiFactory(
            await CreateDatabaseAsync(admin, "touchline_partial_v033", FlywayBaseline, PartialV033), Tokens);
    }

    public async Task DisposeAsync()
    {
        ReadyApi?.Dispose();
        PreV033Api?.Dispose();
        PartialV033Api?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(ReadyConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        }
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> CreateDatabaseAsync(string adminConnectionString, string name, params string[] scripts)
    {
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ToString();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var script in scripts)
        {
            await using var cmd = new NpgsqlCommand(script, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        return connectionString;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Touchline.sln")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not find Touchline.sln above " + AppContext.BaseDirectory);
    }
}
