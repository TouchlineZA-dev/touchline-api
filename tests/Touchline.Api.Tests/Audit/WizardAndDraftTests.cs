using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>Part 2: wizard step validation (never saves) and drafts (save, resume, discard, stale, expiry, ownership).</summary>
public sealed class WizardAndDraftTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private static JsonElement Position(JsonElement check, string position) =>
        check.GetProperty("positions").EnumerateArray().Single(p => p.GetProperty("position").GetString() == position);

    // ---- Step validation ----------------------------------------------------------------------

    [SkippableFact]
    public async Task W01_squad_check_reports_live_progress_for_a_partial_squad_and_never_saves()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = NewManager(f, out _);   // no team yet: the onboarding wizard can still check
        var players = new[] { "c0-gk1", "c1-def1", "c2-def1", "c3-def1", "c4-def5", "c9-att3" }.Select(k => pool[k]).ToArray();
        var check = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", new { players }));

        Assert.False(check.GetProperty("complete").GetBoolean());
        Assert.False(check.GetProperty("valid").GetBoolean());
        Assert.Equal(4, Position(check, "DEF").GetProperty("have").GetInt32());
        Assert.Equal(5, Position(check, "DEF").GetProperty("need").GetInt32());
        Assert.Equal(1, Position(check, "GK").GetProperty("have").GetInt32());
        Assert.Equal(100.0m - (4.0m + 3 * 4.0m + 6.5m + 10.5m), check.GetProperty("budget").GetProperty("left").GetDecimal());   // R67.0m left
        Assert.Empty(check.GetProperty("violations").EnumerateArray());
        Assert.Contains(check.GetProperty("nextSteps").EnumerateArray(), s => s.GetString() == "Pick 1 more defender.");

        // Nothing was saved anywhere.
        Assert.True((await Ok(await client.GetAsync("/api/v1/me"))).GetProperty("onboarding").GetProperty("needsTeam").GetBoolean());
    }

    [SkippableFact]
    public async Task W02_squad_check_names_broken_rules_as_soon_as_they_happen()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = NewManager(f, out _);
        async Task<List<string>> Rules(string[] keys)
        {
            var c = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", new { players = keys.Select(k => pool[k]).ToArray() }));
            return c.GetProperty("violations").EnumerateArray().Select(v => v.GetProperty("rule").GetString()!).ToList();
        }
        Assert.Contains("club_limit", await Rules(["c1-def1", "c1-def2", "c1-mid1", "c1-att1"]));
        Assert.Contains("position_count", await Rules(["c0-gk1", "c1-gk1", "c2-gk1"]));
        Assert.Contains("duplicate_player", await Rules(["c0-gk1", "c0-gk1"]));
        Assert.Contains("player_unavailable", await Rules(["x-att"]));
        // 3 × R10.5m + 5 × R9.5m + 5 × R6.5m + 2 × R4.5m = R120.5m
        Assert.Contains("over_budget", await Rules(["c0-att3", "c1-att3", "c2-att3", "c3-mid5", "c4-mid5", "c5-mid5", "c6-mid5", "c7-mid5",
            "c8-def5", "c9-def5", "c10-def5", "c11-def5", "c12-def5", "c13-gk2", "c14-gk2"]));
    }

    [SkippableFact]
    public async Task W03_a_complete_squad_check_matches_the_save_and_reports_formation_bench_and_captaincy()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithTeamAsync(f);
        var ok = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", Exactly100().Request(pool, null)));
        Assert.True(ok.GetProperty("valid").GetBoolean(), ok.ToString());
        Assert.Equal(0.0m, ok.GetProperty("budget").GetProperty("left").GetDecimal());
        Assert.Equal("4-4-2", ok.GetProperty("formation").GetProperty("actual").GetString());
        Assert.True(ok.GetProperty("lineup").GetProperty("valid").GetBoolean());
        Assert.True(ok.GetProperty("captaincy").GetProperty("valid").GetBoolean());

        var bad = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", (Exactly100() with { Vice = "c9-att3" }).Swap("c2-def1", "c2-def2").Request(pool, null)));
        Assert.False(bad.GetProperty("valid").GetBoolean());
        var rules = bad.GetProperty("violations").EnumerateArray().Select(v => v.GetProperty("rule").GetString()).ToList();
        Assert.Contains("over_budget", rules);
        Assert.Contains("captain_vice_same", rules);
        Assert.False(bad.GetProperty("captaincy").GetProperty("valid").GetBoolean());
    }

    [SkippableFact]
    public async Task W04_lineup_check_flags_formation_and_bench_problems_without_saving()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var version = (await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"))).GetProperty("version").GetInt32();
        var gkInMidfield = Cheap().Swap("c5-mid1", "TMP").Swap("c11-gk1", "c5-mid1").Swap("TMP", "c11-gk1");
        var check = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/lineup/validate", gkInMidfield.Request(pool, null)));
        Assert.False(check.GetProperty("valid").GetBoolean());
        Assert.Contains(check.GetProperty("violations").EnumerateArray(), v => v.GetProperty("rule").GetString() == "bench_gk_swap");
        Assert.Equal(version, (await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"))).GetProperty("version").GetInt32());

        var partial = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/lineup/validate", new { formation = "4-4-2", starters = new[] { pool["c0-gk1"] } }));
        Assert.False(partial.GetProperty("complete").GetBoolean());
        Assert.Empty(partial.GetProperty("violations").EnumerateArray());
        Assert.Contains(partial.GetProperty("nextSteps").EnumerateArray(), s => s.GetString()!.StartsWith("Choose 10 more starters"));
    }

    [SkippableFact]
    public async Task W05_transfer_check_handles_an_unfinished_set_and_prices_a_complete_one()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var half = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/validate", new { @out = new[] { pool["c1-def1"] } }));
        Assert.False(half.GetProperty("complete").GetBoolean());
        Assert.Equal(4, Position(half, "DEF").GetProperty("have").GetInt32());
        Assert.Contains(half.GetProperty("nextSteps").EnumerateArray(), s => s.GetString() == "Buy 1 defender.");
        Assert.Equal(37.0m, half.GetProperty("budget").GetProperty("left").GetDecimal());   // 33.0 + 4.0 sold

        var done = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/validate", new { @out = new[] { pool["c1-def1"] }, @in = new[] { pool["c15-def5"] } }));
        Assert.True(done.GetProperty("valid").GetBoolean(), done.ToString());
        Assert.Equal(30.5m, done.GetProperty("budget").GetProperty("left").GetDecimal());
        Assert.True(done.GetProperty("transfers").GetProperty("unlimited").GetBoolean());   // the team's first week
        Assert.Equal(0, done.GetProperty("transfers").GetProperty("pointsHit").GetInt32());

        var wrong = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/validate", new { @out = new[] { pool["c1-def1"] }, @in = new[] { pool["c15-mid1"] } }));
        Assert.False(wrong.GetProperty("valid").GetBoolean());
        Assert.Contains(wrong.GetProperty("violations").EnumerateArray(), v => v.GetProperty("rule").GetString() == "transfer_position_mismatch");
    }

    // ---- Drafts ---------------------------------------------------------------------------------

    [SkippableFact]
    public async Task R01_save_load_overwrite_and_a_stale_version_is_409_draft_stale_with_the_current_draft()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var phone = NewManager(f, out var sub);
        var tablet = f.Api.ClientWithToken(f.Tokens.Create(sub, null));   // same manager, second device

        var v1 = await Ok(await phone.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { players = new[] { 1, 2 } }, version = 0, step = "pick" }));
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        var loaded = await Ok(await tablet.GetAsync("/api/v1/me/drafts/squad"));
        Assert.Equal("pick", loaded.GetProperty("step").GetString());
        Assert.Equal(2, loaded.GetProperty("payload").GetProperty("players").GetArrayLength());

        var v2 = await Ok(await tablet.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { players = new[] { 1, 2, 3 } }, version = 1 }));
        Assert.Equal(2, v2.GetProperty("version").GetInt32());

        // The phone still thinks it's at version 1.
        var stale = await Send(phone.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { players = new[] { 9 } }, version = 1 }));
        AssertError(stale, HttpStatusCode.Conflict, "draft_stale");
        Assert.Equal(3, stale.Body.GetProperty("current").GetProperty("payload").GetProperty("players").GetArrayLength());
        // Starting "fresh" over a live draft is also refused, so it can't silently overwrite.
        AssertError(await Send(phone.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { }, version = 0 })), HttpStatusCode.Conflict, "draft_stale");
        // Two saves of the same version at once: exactly one wins.
        var race = await Task.WhenAll(
            phone.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { who = "phone" }, version = 2 }),
            tablet.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { who = "tablet" }, version = 2 }));
        Assert.Single(race, r => r.IsSuccessStatusCode);
        Assert.Single(race, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task R02_clearing_is_an_update_never_a_delete_and_a_cleared_draft_can_start_again()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var client = NewManager(f, out var sub);
        await Ok(await client.PutAsJsonAsync("/api/v1/me/drafts/league", new { payload = new { name = "My league" }, version = 0 }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/me/drafts/league")).StatusCode);
        AssertError(await Send(client.GetAsync("/api/v1/me/drafts/league")), HttpStatusCode.NotFound, "draft_not_found");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/me/drafts/league")).StatusCode);   // idempotent

        // The row is still there, marked cleared and emptied.
        var row = await QueryAsync(f.Db, $"""
            SELECT d.cleared_at IS NOT NULL, d.payload::text FROM app.wizard_drafts d JOIN app.users u ON u.id = d.user_id
            WHERE u.auth_user_id = '{sub}' AND d.kind = 'league'
            """, r => (r.GetBoolean(0), r.GetString(1)));
        Assert.Equal((true, "{}"), Assert.Single(row));
        Assert.False(await f.Db.ScalarAsync<bool>("SELECT has_table_privilege('app_api_user', 'app.wizard_drafts', 'DELETE')"));

        var again = await Ok(await client.PutAsJsonAsync("/api/v1/me/drafts/league", new { payload = new { name = "Again" }, version = 0 }));
        Assert.Equal("Again", again.GetProperty("payload").GetProperty("name").GetString());
    }

    [SkippableFact]
    public async Task R03_drafts_expire_and_are_validated_by_kind_shape_and_size()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var client = NewManager(f, out var sub);
        await Ok(await client.PutAsJsonAsync("/api/v1/me/drafts/transfers", new { payload = new { @out = new[] { 1 } }, version = 0 }));
        // Expired by the API's clock (the test clock), not the database's: the test clock is fixed when the class starts.
        await f.Db.ExecAsync($"UPDATE app.wizard_drafts SET expires_at = $1 WHERE user_id = (SELECT id FROM app.users WHERE auth_user_id = '{sub}')",
            f.Clock.Now.AddMinutes(-1).UtcDateTime);
        AssertError(await Send(client.GetAsync("/api/v1/me/drafts/transfers")), HttpStatusCode.NotFound, "draft_not_found");
        Assert.Empty((await Ok(await client.GetAsync("/api/v1/me/drafts"))).GetProperty("drafts").EnumerateArray());
        await Ok(await client.PutAsJsonAsync("/api/v1/me/drafts/transfers", new { payload = new { }, version = 0 }));   // expired = start again

        AssertError(await Send(client.PutAsJsonAsync("/api/v1/me/drafts/free_hit", new { payload = new { }, version = 0 })), HttpStatusCode.BadRequest, "rule_violation", "draft_kind");
        AssertError(await Send(client.PutAsJsonAsync("/api/v1/me/drafts/lineup", new { payload = new[] { 1, 2 }, version = 0 })), HttpStatusCode.BadRequest, "rule_violation", "draft_payload");
        // Anything over 64 KB is stopped by the API's request-size guard before it reaches the draft.
        AssertError(await Send(client.PutAsJsonAsync("/api/v1/me/drafts/lineup", new { payload = new { blob = new string('x', 70_000) }, version = 0 })), HttpStatusCode.RequestEntityTooLarge, "request_too_large");
    }

    [SkippableFact]
    public async Task R04_a_squad_draft_is_never_a_saved_squad_and_is_never_scored()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithTeamAsync(f, "Draft Only");
        await Ok(await client.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = Cheap().Request(pool, pool.NextGameweek), version = 0 }));
        var me = await Ok(await client.GetAsync("/api/v1/me"));
        Assert.True(me.GetProperty("onboarding").GetProperty("needsSquad").GetBoolean());
        Assert.NotEqual("done", me.GetProperty("onboarding").GetProperty("step").GetString());
        Assert.Equal(["squad"], me.GetProperty("onboarding").GetProperty("drafts").EnumerateArray().Select(d => d.GetString()));
        Assert.Empty((await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"))).GetProperty("players").EnumerateArray());
        var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
        Assert.Equal(0L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_squads WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));
        Assert.Equal(0L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));
        Assert.Equal(100.0m, team.GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public async Task R05_before_migration_015_drafts_are_503_drafts_unavailable_and_everything_else_works()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        Skip.If(server is null, skip);
        var db = await TestDatabase.CreateAsync(server!, "pre_v051", upToVersion: 50);
        var competition = await db.ScalarAsync<int>("INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'PSL') RETURNING id");
        await db.ExecAsync("INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) VALUES (28123, $1, '2026/2027', true)", competition);
        var tokens = new TestTokens();
        using var api = new TestApiFactory(db.ApiConnectionString, tokens);
        var client = api.ClientWithToken(tokens.Create(Guid.NewGuid(), null));

        var me = await Ok(await client.GetAsync("/api/v1/me"));
        Assert.False(me.GetProperty("onboarding").GetProperty("hasDraft").GetBoolean());
        Assert.Empty(me.GetProperty("onboarding").GetProperty("drafts").EnumerateArray());
        AssertError(await Send(client.PutAsJsonAsync("/api/v1/me/drafts/squad", new { payload = new { }, version = 0 })), HttpStatusCode.ServiceUnavailable, "drafts_unavailable");
        AssertError(await Send(client.GetAsync("/api/v1/me/drafts")), HttpStatusCode.ServiceUnavailable, "drafts_unavailable");
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Still Works" }));
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", new { players = Array.Empty<int>() }));
    }
}
