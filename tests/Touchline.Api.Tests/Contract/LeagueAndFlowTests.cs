using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.SafeWrites.FantasyWorld;

namespace Touchline.Api.Tests.Contract;

/// <summary>Sprint 6 league rules (03 Stage 6 list) and the staging kit, through the real pipeline.</summary>
public sealed class LeagueTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private async Task<HttpClient> Manager(string team)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var client = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = team }));
        return client;
    }

    private static async Task<(int Id, string Code)> CreateLeague(HttpClient owner, string name)
    {
        var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name }));
        return (int.Parse(league.GetProperty("id").GetString()!), league.GetProperty("joinCode").GetString()!);
    }

    [SkippableFact]
    public async Task Concurrent_joins_at_the_cap_give_one_winner_and_a_clear_answer_to_the_other()
    {
        var owner = await Manager("Cap Owner");
        var (id, code) = await CreateLeague(owner, "Cap League");
        await f.Db.ExecAsync("UPDATE app.leagues SET max_members = 2 WHERE id = $1", id);
        var a = await Manager("Joiner A");
        var b = await Manager("Joiner B");
        var results = await Task.WhenAll(
            a.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }),
            b.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("full", await loser.Content.ReadAsStringAsync());
        Assert.Equal(2L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.league_members WHERE league_id = $1 AND left_at IS NULL", id));
    }

    [SkippableFact]
    public async Task Repeated_joins_and_rejoins_keep_one_membership_per_team()
    {
        var owner = await Manager("Repeat Owner");
        var (id, code) = await CreateLeague(owner, "Repeat League");
        var fan = await Manager("Repeat Fan");
        await Ok(await fan.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }));
        Assert.Equal(HttpStatusCode.Conflict, (await fan.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await fan.PostAsync($"/api/v1/fantasy/leagues/{id}/leave", null)).StatusCode);
        await Ok(await fan.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }));
        var team = await Ok(await fan.GetAsync("/api/v1/fantasy/team"));
        Assert.Equal(1L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.league_members WHERE league_id = $1 AND fantasy_team_id = $2", id, team.GetProperty("id").GetInt32()));
    }

    [SkippableFact]
    public async Task The_owner_cannot_leave_but_can_hand_over_then_leave_and_the_new_owner_can_archive()
    {
        var owner = await Manager("Departing Owner");
        var (id, code) = await CreateLeague(owner, "Handover League");
        var heir = await Manager("Heir");
        await Ok(await heir.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }));
        var heirTeam = (await Ok(await heir.GetAsync("/api/v1/fantasy/team"))).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/v1/fantasy/leagues/{id}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await heir.PostAsJsonAsync($"/api/v1/fantasy/leagues/{id}/owner", new { fantasyTeamId = heirTeam })).StatusCode);
        await Ok(await owner.PostAsJsonAsync($"/api/v1/fantasy/leagues/{id}/owner", new { fantasyTeamId = heirTeam }));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/fantasy/leagues/{id}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await heir.PostAsync($"/api/v1/fantasy/leagues/{id}/archive", null)).StatusCode);
        Assert.False(await f.Db.ScalarAsync<bool>("SELECT is_active FROM app.leagues WHERE id = $1", id));
        Assert.Equal(1L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.leagues WHERE id = $1", id));   // archived, not deleted
    }

    [SkippableFact]
    public async Task Tied_ranks_are_deterministic_and_members_see_ranks_but_never_each_others_squads()
    {
        var pool = await EnsureAsync(f);
        var a = await ManagerWithSquadAsync(f, pool);
        var b = await ManagerWithSquadAsync(f, pool);
        var (id, code) = await CreateLeague(a, "Tie League");
        await Ok(await b.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }));
        var outsider = await Manager("Outsider");

        var standings = await Ok(await b.GetAsync($"/api/v1/fantasy/leagues/{id}/standings"));
        var rows = standings.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([1, 2], rows.Select(r => r.GetProperty("rank").GetInt32()));   // level on points: ties go to overall points, then username
        Assert.All(rows, r => Assert.Equal(["rank", "fantasyTeamId", "teamName", "username", "points", "overallPoints", "provisional", "previousRank", "movement"],
            r.EnumerateObject().Select(p => p.Name)));   // no picks, captaincy or chips
        var again = await Ok(await a.GetAsync($"/api/v1/fantasy/leagues/{id}/standings"));
        Assert.Equal(rows.Select(r => r.GetProperty("fantasyTeamId").GetInt32()), again.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("fantasyTeamId").GetInt32()));

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/v1/fantasy/leagues/{id}/standings")).StatusCode);
        // The only squad endpoint is my own; a rival's unpublished picks have no route at all.
        var mine = await Ok(await b.GetAsync("/api/v1/fantasy/team/squad"));
        Assert.Equal((await Ok(await b.GetAsync("/api/v1/fantasy/team"))).GetProperty("id").GetInt32(), mine.GetProperty("teamId").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/fantasy/team/{rows[0].GetProperty("fantasyTeamId").GetInt32()}/squad")).StatusCode);
    }

    [SkippableFact]
    public async Task The_staging_kit_resets_the_reference_league_by_archiving_and_recreating()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        using var api = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["StagingKit:Enabled"] = "true" });
        var sub = Guid.NewGuid();
        var admin = api.ClientWithToken(f.Tokens.Create(sub, null));
        var me = await Ok(await admin.GetAsync("/api/v1/me"));
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", me.GetProperty("id").GetInt32());

        var first = await Ok(await admin.PostAsync("/api/v1/admin/staging-kit/reset", null));
        var second = await Ok(await admin.PostAsync("/api/v1/admin/staging-kit/reset", null));
        Assert.Equal(2, second.GetProperty("managers").GetArrayLength());
        Assert.Equal(first.GetProperty("managers")[0].GetProperty("teamId").GetInt32(), second.GetProperty("managers")[0].GetProperty("teamId").GetInt32());
        Assert.Equal([first.GetProperty("league").GetProperty("id").GetInt32()],
            second.GetProperty("league").GetProperty("archivedLeagueIds").EnumerateArray().Select(x => x.GetInt32()));

        // Disabled (the default): the route answers 404 even for an admin.
        Assert.Equal(HttpStatusCode.NotFound, (await f.Api.ClientWithToken(f.Tokens.Create(sub, null)).PostAsync("/api/v1/admin/staging-kit/reset", null)).StatusCode);
    }
}

/// <summary>
/// 02's release gate (shared team across two clients) and the Sprint 6 "pass when" flow, using only
/// documented routes and no manual database edits by the clients (the test plays the worker and sets one admin flag).
/// </summary>
public sealed class FrontendFlowTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task Admin_creates_a_gameweek_then_two_clients_see_the_same_team_picks_and_balance_and_a_stale_edit_gets_409()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var adminSub = Guid.NewGuid();
        var admin = f.Api.ClientWithToken(f.Tokens.Create(adminSub, null));
        var adminMe = await Ok(await admin.GetAsync("/api/v1/me"));
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", adminMe.GetProperty("id").GetInt32());
        var start = f.Clock.Now.AddDays(40);
        await f.FixtureAsync(start.AddDays(1), 1, null, null, pool.Clubs[0], pool.Clubs[1]);
        var created = await Ok(await admin.PostAsJsonAsync("/api/v1/admin/gameweeks", new
        {
            seasonId = f.SeasonId, name = "API-created GW", startDate = DateOnly.FromDateTime(start.UtcDateTime), endDate = DateOnly.FromDateTime(start.AddDays(6).UtcDateTime),
        }));
        Assert.Equal(start.AddDays(1).AddMinutes(-90), created.GetProperty("deadline").GetDateTimeOffset());   // editable later, defaults to 90 min before

        var sub = Guid.NewGuid();
        var phone = f.Api.ClientWithToken(f.Tokens.Create(sub, $"shared-{sub:N}@example.com"));
        var web = f.Api.ClientWithToken(f.Tokens.Create(sub, $"shared-{sub:N}@example.com"));
        var me = await Ok(await phone.GetAsync("/api/v1/me"));
        var team = await Ok(await phone.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Shared XI" }));
        var saved = await Ok(await phone.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        Assert.Equal(15.0m, saved.GetProperty("budgetRemaining").GetDecimal());   // R100.0m − R85.0m squad

        var snapshot = await Ok(await web.GetAsync("/api/v1/fantasy/team/snapshot"));
        Assert.Equal(me.GetProperty("id").GetInt32(), (await Ok(await web.GetAsync("/api/v1/me"))).GetProperty("id").GetInt32());
        Assert.Equal(team.GetProperty("id").GetInt32(), snapshot.GetProperty("team").GetProperty("id").GetInt32());
        Assert.Equal(15.0m, snapshot.GetProperty("team").GetProperty("budgetRemaining").GetDecimal());
        Assert.Equal(saved.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("playerId").GetInt32()).Order(),
            snapshot.GetProperty("squad").GetProperty("players").EnumerateArray().Select(p => p.GetProperty("playerId").GetInt32()).Order());

        // The web client edits with the version it loaded; the phone then edits with the same, now stale, version.
        var etag = (await web.GetAsync("/api/v1/fantasy/team/squad")).Headers.ETag!.Tag;
        HttpRequestMessage Edit(int captain)
        {
            var body = JsonSerializer.SerializeToNode(Squad(pool, pool.NextGameweek))!.AsObject();
            body["captainId"] = captain;
            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/fantasy/team/lineup") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            return request;
        }
        await Ok(await web.SendAsync(Edit(pool.Players["c3-mid1"])));
        var stale = await phone.SendAsync(Edit(pool.Players["c4-mid1"]));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("stale_squad", problem.GetProperty("code").GetString());
        Assert.Equal(pool.Players["c3-mid1"], problem.GetProperty("current").GetProperty("captainId").GetInt32());   // recoverable: the latest team is attached
    }

    [SkippableFact]
    public async Task Follow_a_fixture_onboard_two_managers_share_a_league_transfer_view_points_and_explain_a_rank_change()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var start = f.Clock.Now;
        var kickoff = start.AddDays(4);
        var fixture = await f.FixtureAsync(kickoff, 1, null, null, pool.Clubs[4], pool.Clubs[5]);
        foreach (var (key, sm) in pool.SportmonksIds.Where(kv => kv.Key.StartsWith("c4-") || kv.Key.StartsWith("c5-")))
        {
            await f.LineupAsync(fixture, key.StartsWith("c4-") ? pool.Clubs[4] : pool.Clubs[5], sm, 11);
        }

        // 1. Follow the fixture: read it, then revalidate cheaply with the ETag.
        var anon = f.Api.CreateClient();
        var first = await anon.GetAsync($"/api/v1/fixtures/{fixture}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var revalidate = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/fixtures/{fixture}");
        revalidate.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(first.Headers.ETag!.Tag));
        Assert.Equal(HttpStatusCode.NotModified, (await anon.SendAsync(revalidate)).StatusCode);

        // 2. Onboard two managers: username, team, squad.
        async Task<HttpClient> Onboard(string username)
        {
            var client = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
            await Ok(await client.PatchAsync("/api/v1/me", JsonContent.Create(new { username })));
            await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = $"{username} XI" }));
            await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
            return client;
        }
        var alice = await Onboard($"alice_{Guid.NewGuid():N}"[..14]);
        var bob = await Onboard($"bob_{Guid.NewGuid():N}"[..12]);

        // 3. One league.
        var league = await Ok(await alice.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Flow League" }));
        await Ok(await bob.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code = league.GetProperty("joinCode").GetString() }));
        var leagueId = league.GetProperty("id").GetString();

        // 4. Alice transfers: c4-mid1 out, c5-mid2 in.
        var quote = await Ok(await alice.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview",
            new { gameweekId = pool.NextGameweek, @out = new[] { pool.Players["c4-mid1"] }, @in = new[] { pool.Players["c5-mid2"] } }));
        var confirm = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fantasy/team/transfers/confirm")
        {
            Content = JsonContent.Create(new
            {
                gameweekId = pool.NextGameweek, @out = new[] { pool.Players["c4-mid1"] }, @in = new[] { pool.Players["c5-mid2"] },
                squadVersion = quote.GetProperty("squadVersion").GetInt32(), priceRevision = quote.GetProperty("priceRevision").GetInt32(),
                quotedAtUtc = quote.GetProperty("quotedAtUtc").GetDateTimeOffset(),
            }),
        };
        confirm.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await Ok(await alice.SendAsync(confirm));

        try
        {
            // 5. The match is played (the test plays the worker): c5-mid2 scores, 1-0 to club 5.
            f.Clock.Now = kickoff.AddHours(3);
            await f.SetFixtureAsync(fixture, 5, 0, 1);
            await f.EventAsync(fixture, FootballDbFixture.NextId(), 14, pool.Clubs[5], pool.SportmonksIds["c5-mid2"], null, 30);

            var alicePoints = await Ok(await alice.GetAsync("/api/v1/fantasy/team/points"));
            var bobPoints = await Ok(await bob.GetAsync("/api/v1/fantasy/team/points"));
            Assert.True(alicePoints.GetProperty("provisional").GetBoolean());
            Assert.True(alicePoints.GetProperty("total").GetInt32() > bobPoints.GetProperty("total").GetInt32());

            // 6. The operator settles the week (documented admin routes), then the rank change is explained.
            var adminSub = Guid.NewGuid();
            var admin = f.Api.ClientWithToken(f.Tokens.Create(adminSub, null));
            var adminMe = await Ok(await admin.GetAsync("/api/v1/me"));
            await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", adminMe.GetProperty("id").GetInt32());
            var settlement = await Ok(await admin.GetAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/settlement"));
            foreach (var fx in settlement.GetProperty("fixtures").EnumerateArray().Where(x => x.GetProperty("blocking").GetBoolean()))
            {
                await Ok(await admin.PostAsJsonAsync($"/api/v1/admin/fixtures/{fx.GetProperty("fixtureId").GetInt32()}/resolution",
                    new { outcome = fx.GetProperty("events").GetInt32() == 0 ? "verified_zero" : "manual_resolution", reason = "Checked" }));
            }
            await Ok(await admin.PostAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/score", null));

            var standings = await Ok(await bob.GetAsync($"/api/v1/fantasy/leagues/{leagueId}/standings"));
            var aliceTeam = (await Ok(await alice.GetAsync("/api/v1/fantasy/team"))).GetProperty("id").GetInt32();
            Assert.Equal(aliceTeam, standings.GetProperty("rows")[0].GetProperty("fantasyTeamId").GetInt32());
            Assert.StartsWith("r", standings.GetProperty("revision").GetString());

            var explain = await Ok(await alice.GetAsync("/api/v1/fantasy/team/points/explain"));
            Assert.False(explain.GetProperty("provisional").GetBoolean());
            var scorer = explain.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("playerId").GetInt32() == pool.Players["c5-mid2"]);
            Assert.Contains(scorer.GetProperty("fixtures")[0].GetProperty("items").EnumerateArray(), i => i.GetProperty("label").GetString()!.Contains("goal"));
            var history = await Ok(await bob.GetAsync($"/api/v1/fantasy/leagues/{leagueId}/history"));
            Assert.Equal(2, history.GetProperty("history").EnumerateArray().Last().GetProperty("rank").GetInt32());
        }
        finally
        {
            f.Clock.Now = start;
        }
    }
}
