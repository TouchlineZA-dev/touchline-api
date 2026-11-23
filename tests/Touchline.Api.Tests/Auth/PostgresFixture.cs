using Npgsql;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// Three isolated databases built from the canonical touchline-sync Flyway migrations
/// (Db/TestDatabase.cs), the API connecting as app_api_user with its real grants:
///   ready       - every migration (V001–V038 pinned, plus this repo's pending ones): a clean install
///   pre_v033    - V001–V032: before the Supabase auth link
///   partial     - V001–V032 + auth_user_id, but password_hash still NOT NULL (V033 half-applied)
/// Never touches a shared or production database.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string PartialV033 = """
        ALTER TABLE app.users ADD COLUMN auth_user_id UUID;
        ALTER TABLE app.users ADD CONSTRAINT uq_users_auth_user_id UNIQUE (auth_user_id);
        """;

    public string? SkipReason { get; private set; }
    public TestTokens Tokens { get; } = new();

    public TestDb Ready { get; private set; } = null!;

    /// <summary>The API's connection (app_api_user) to the ready database.</summary>
    public string ReadyConnectionString => Ready.ApiConnectionString;
    public TestApiFactory ReadyApi { get; private set; } = null!;
    public TestApiFactory PreV033Api { get; private set; } = null!;
    public TestApiFactory PartialV033Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        if (server is null)
        {
            SkipReason = skip;
            return;
        }

        Ready = await TestDatabase.CreateAsync(server, "auth_ready");
        ReadyApi = new TestApiFactory(Ready.ApiConnectionString, Tokens);
        PreV033Api = new TestApiFactory((await TestDatabase.CreateAsync(server, "auth_pre_v033", upToVersion: 32)).ApiConnectionString, Tokens);
        PartialV033Api = new TestApiFactory(
            (await TestDatabase.CreateAsync(server, "auth_partial", 32, PartialV033)).ApiConnectionString, Tokens);
    }

    public Task DisposeAsync()
    {
        ReadyApi?.Dispose();
        PreV033Api?.Dispose();
        PartialV033Api?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Test setup and assertions run as the database admin (they may touch football rows).</summary>
    public Task<T> ScalarAsync<T>(string sql, params object[] args) => Ready.ScalarAsync<T>(sql, args);

    /// <summary>A new PSL-like season (football rows are seeded as admin, the way the worker would).</summary>
    public Task<int> NewSeasonAsync() => Ready.ScalarAsync<int>("""
        WITH c AS (
            INSERT INTO football.competitions (sportmonks_competition_id, name)
            VALUES ((floor(random() * 1e9))::bigint + 10000, 'Test competition') RETURNING id)
        INSERT INTO football.seasons (sportmonks_season_id, competition_id, name)
        SELECT (floor(random() * 1e9))::int + 10000, id, '2026/2027' FROM c RETURNING id
        """);
}
