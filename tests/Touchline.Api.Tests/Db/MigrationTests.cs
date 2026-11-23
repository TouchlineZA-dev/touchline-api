using System.Net;
using Touchline.Api.Tests.Auth;

namespace Touchline.Api.Tests.Db;

/// <summary>
/// The canonical migrations on a disposable database with the real app_api_user grants:
/// a clean install from empty, and a populated upgrade (V038 with data, then this repo's pending
/// migrations), rerun safely.
/// </summary>
public sealed class MigrationTests
{
    private static async Task<string> ServerOrSkip()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        Skip.If(server is null, skip);
        return server!;
    }

    [SkippableFact]
    public async Task Clean_install_applies_every_migration_and_the_api_role_cannot_delete_or_write_football()
    {
        var db = await TestDatabase.CreateAsync(await ServerOrSkip(), "mig_clean");

        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('app.fixture_gameweek_assignments') IS NOT NULL"));
        Assert.True(await db.ScalarAsync<bool>("SELECT has_table_privilege('app_api_user', 'app.fantasy_teams', 'INSERT')"));
        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('app_api_user', 'app.fantasy_teams', 'DELETE')"));
        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('app_api_user', 'football.fixtures', 'INSERT')"));
        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('app_api_user', 'sync.gameweek_sync_status', 'UPDATE')"));
        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('app_api_user', 'app.fixture_gameweek_assignments', 'DELETE')"));

        // The API itself starts and answers on the clean install.
        using var api = new TestApiFactory(db.ApiConnectionString, new TestTokens());
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/api/v1/gameweeks")).StatusCode);
    }

    [SkippableFact]
    public async Task Populated_upgrade_backfills_numbers_and_assignments_keeps_data_and_is_safe_to_rerun()
    {
        var db = await TestDatabase.CreateAsync(await ServerOrSkip(), "mig_upgrade", upToVersion: 38);
        var season = await db.ScalarAsync<int>("""
            WITH c AS (INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'PSL') RETURNING id)
            INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) SELECT 28123, id, '2026/2027', true FROM c RETURNING id
            """);
        var comp = await db.ScalarAsync<int>("SELECT competition_id FROM football.seasons WHERE id = $1", season);
        var gw2 = await db.ScalarAsync<int>("INSERT INTO app.gameweeks (name, start_date, end_date, season_id) VALUES ('Gameweek 2', '2026-08-10', '2026-08-16', $1) RETURNING id", season);
        var gw1 = await db.ScalarAsync<int>("INSERT INTO app.gameweeks (name, start_date, end_date, season_id) VALUES ('Gameweek 1', '2026-07-27', '2026-08-02', $1) RETURNING id", season);
        // 22:30 UTC on 2 Aug is 00:30 SAST on 3 Aug: outside GW1 by SAST date.
        var inGw1 = await Fixture(db, comp, season, "2026-08-01 13:00+00", 5);
        var lateNight = await Fixture(db, comp, season, "2026-08-02 22:30+00", 5);
        var postponed = await Fixture(db, comp, season, "2026-08-12 17:00+00", 10);
        var inGw2 = await Fixture(db, comp, season, "2026-08-12 17:00+00", 5);
        var user = await db.ScalarAsync<int>("INSERT INTO app.users (email, username) VALUES ('u@example.com', 'u') RETURNING id");
        var team = await db.ScalarAsync<int>("INSERT INTO app.fantasy_teams (user_id, season_id, name) VALUES ($1, $2, 'Kept XI') RETURNING id", user, season);

        foreach (var pass in new[] { 1, 2 })
        {
            foreach (var (_, sql) in TestDatabase.Migrations().Where(m => m.Version > 38)) await db.ExecAsync(sql);
        }

        Assert.Equal(1, await db.ScalarAsync<short>("SELECT number FROM app.gameweeks WHERE id = $1", gw1));
        Assert.Equal(2, await db.ScalarAsync<short>("SELECT number FROM app.gameweeks WHERE id = $1", gw2));
        Assert.Equal(gw1, await CurrentGameweek(db, inGw1));
        Assert.Equal(gw2, await CurrentGameweek(db, inGw2));
        Assert.Null(await CurrentGameweek(db, lateNight));
        Assert.Null(await CurrentGameweek(db, postponed));   // postponed fixtures aren't backfilled (default decision 1)
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM app.fixture_gameweek_assignments"));   // rerun added nothing
        Assert.Equal("Kept XI", await db.ScalarAsync<string>("SELECT name FROM app.fantasy_teams WHERE id = $1", team));
    }

    [SkippableFact]
    public async Task Before_the_calendar_migration_the_api_falls_back_to_date_windows()
    {
        var db = await TestDatabase.CreateAsync(await ServerOrSkip(), "mig_pre007", upToVersion: 38);
        await db.ExecAsync("""
            WITH c AS (INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'PSL') RETURNING id)
            INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) SELECT 28123, id, '2026/2027', true FROM c
            """);
        await db.ExecAsync("INSERT INTO app.gameweeks (name, start_date, end_date, season_id) SELECT 'Gameweek 1', '2026-07-27', '2026-08-02', id FROM football.seasons");

        using var api = new TestApiFactory(db.ApiConnectionString, new TestTokens());
        var response = await api.CreateClient().GetAsync("/api/v1/gameweeks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"number\":null", await response.Content.ReadAsStringAsync());
    }

    private static Task<int> Fixture(TestDb db, int comp, int season, string kickoff, short state) => db.ScalarAsync<int>(
        $"INSERT INTO football.fixtures (sportmonks_fixture_id, competition_id, season_id, state_id, starting_at) VALUES ((floor(random()*1e9))::int, $1, $2, $3, '{kickoff}') RETURNING id",
        comp, season, state);

    private static async Task<int?> CurrentGameweek(TestDb db, int fixtureId)
    {
        var value = await db.ScalarAsync<object?>(
            "SELECT gameweek_id FROM app.fixture_gameweek_assignments WHERE fixture_id = $1 ORDER BY id DESC LIMIT 1", fixtureId);
        return value is int id ? id : null;
    }
}
