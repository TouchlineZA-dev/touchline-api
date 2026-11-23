using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using Touchline.Api.Tests.Reference;
using static Touchline.Api.Tests.SafeWrites.FantasyWorld;

namespace Touchline.Api.Tests.Security;

/// <summary>Sprint 7 (03 Stage 7, handbook Gate 9): identity, access matrix, limits, exposure, audit, switches.</summary>
public sealed class SecurityTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private async Task<(HttpClient Client, int UserId)> SignedIn(bool admin = false, TestApiFactory? api = null)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var client = (api ?? f.Api).ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        var me = await Ok(await client.GetAsync("/api/v1/me"));
        var id = me.GetProperty("id").GetInt32();
        if (admin) await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", id);
        return (client, id);
    }

    [SkippableFact]
    public async Task A_rotated_signing_key_is_picked_up_from_jwks_and_old_tokens_keep_working()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var tokens = new TestTokens();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var api = new TestApiFactory(f.Db.ApiConnectionString, tokens, clock);
        var oldToken = tokens.Create(Guid.NewGuid(), null);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientWithToken(oldToken).GetAsync("/api/v1/me")).StatusCode);

        tokens.Rotate("test-key-2");
        clock.Now = clock.Now.AddMinutes(2);   // past the 1-minute refetch throttle
        var newToken = tokens.Create(Guid.NewGuid(), null);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientWithToken(newToken).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientWithToken(oldToken).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.ClientWithToken(new TestTokens().Create(Guid.NewGuid(), null)).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public void Production_startup_rejects_incomplete_or_unsafe_configuration()
    {
        IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var production = new FakeEnvironment("Production");
        var good = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Touchline"] = "Host=db;Username=app_api_user.ref;Password=x;SSL Mode=Require",
            ["Supabase:Url"] = "https://ref.supabase.co",
            ["Cors:AllowedOrigins:0"] = "https://app.touchline.co.za",
        };
        Assert.Empty(Touchline.Api.StartupValidation.Problems(Config(good), production));

        var bad = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Touchline"] = "Host=db;Username=postgres;Password=x",
            ["Supabase:Url"] = "http://ref.supabase.co",
            ["Supabase:JwtSecret"] = "short",
            ["Cors:AllowedOrigins:0"] = "*",
            ["StagingKit:Enabled"] = "true",
        };
        var problems = Touchline.Api.StartupValidation.Problems(Config(bad), production);
        Assert.Equal(6, problems.Count);   // SSL, owner role, http Supabase, short secret, wildcard origin, staging kit
        Assert.DoesNotContain(problems, p => p.Contains("Password=x"));   // never echoes values
        Assert.Throws<InvalidOperationException>(() => Touchline.Api.StartupValidation.ThrowIfInvalid(Config(bad), production));
        Touchline.Api.StartupValidation.ThrowIfInvalid(Config(bad), new FakeEnvironment("Development"));   // local development isn't checked
    }

    [SkippableFact]
    public async Task Access_matrix_anonymous_gets_401_managers_get_403_on_every_admin_route_and_each_call_is_audited()
    {
        var (manager, managerId) = await SignedIn();
        var anon = f.Api.CreateClient();
        var admin = f.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("api/v1/admin/", StringComparison.Ordinal))
            .Select(e => (Route: "/" + Regex.Replace(e.RoutePattern.RawText!, @"\{[^}]+\}", "1"),
                          Method: e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods[0]))
            .Distinct().ToList();
        Assert.True(admin.Count >= 15, $"only {admin.Count} admin routes found");

        foreach (var (route, method) in admin)
        {
            HttpRequestMessage Request() => new(new HttpMethod(method), route) { Content = method is "GET" or "DELETE" ? null : JsonContent.Create(new { reason = "probe" }) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Request())).StatusCode);
            var asManager = await manager.SendAsync(Request());
            Assert.True(asManager.StatusCode == HttpStatusCode.Forbidden, $"{method} {route} gave {(int)asManager.StatusCode} to a non-admin");
        }
        var audited = await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.admin_audit_log WHERE actor_user_id = $1 AND outcome_status = 403", managerId);
        Assert.Equal(admin.Count, audited);
    }

    [SkippableFact]
    public async Task Admin_actions_are_audited_with_actor_reason_and_outcome_and_the_scoring_switch_pauses_finalising()
    {
        var (admin, adminId) = await SignedIn(admin: true);
        var pool = await EnsureAsync(f);
        await Ok(await admin.PutAsJsonAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/deadline", new { deadlineAt = f.Clock.Now.AddDays(3), reason = "Kickoff moved" }));
        Assert.Equal("Kickoff moved", await f.Db.ScalarAsync<string>(
            "SELECT reason FROM app.admin_audit_log WHERE actor_user_id = $1 AND action LIKE 'PUT %deadline' ORDER BY id DESC LIMIT 1", adminId));

        using var paused = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["Operations:ScoringPaused"] = "true" });
        var client = paused.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        var me = await Ok(await client.GetAsync("/api/v1/me"));
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", me.GetProperty("id").GetInt32());
        var response = await client.PostAsync($"/api/v1/admin/gameweeks/{pool.PastGameweek}/score", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("scoring_paused", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Account_changes_are_rate_limited_per_user_and_oversized_bodies_are_refused()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        using var api = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["RateLimits:AccountChangesPerMinute"] = "2" });
        var (client, _) = await SignedIn(api: api);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) statuses.Add((await client.PatchAsync("/api/v1/me/notifications", JsonContent.Create(new { goalAlerts = i % 2 == 0 }))).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        var (other, _) = await SignedIn(api: api);   // limits are per user
        Assert.Equal(HttpStatusCode.OK, (await other.PatchAsync("/api/v1/me/notifications", JsonContent.Create(new { goalAlerts = true }))).StatusCode);

        var huge = new StringContent("{\"name\":\"" + new string('x', 70_000) + "\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/v1/fantasy/team", huge)).StatusCode);
    }

    [SkippableFact]
    public async Task Public_reads_are_cached_separately_and_never_carry_emails_join_codes_or_squads()
    {
        var pool = await EnsureAsync(f);
        var ownerSub = Guid.NewGuid();
        var owner = f.Api.ClientWithToken(f.Tokens.Create(ownerSub, $"owner-{ownerSub:N}@example.com"));
        await Ok(await owner.GetAsync("/api/v1/me"));
        await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Exposure XI" }));
        await Ok(await owner.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Private" }));
        var code = league.GetProperty("joinCode").GetString()!;
        var member = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), "member@example.com"));
        await Ok(await member.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Member XI" }));
        await Ok(await member.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code }));

        var anon = f.Api.CreateClient();
        foreach (var path in new[] { "/api/v1/fixtures", "/api/v1/standings", "/api/v1/players", "/api/v1/clubs", "/api/v1/teams", "/api/v1/gameweeks", "/api/v1/rules", "/api/v1/health", "/api/v1/player-rankings" })
        {
            var response = await anon.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotMatch(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[a-z]{2,}", body);
            Assert.DoesNotContain(code, body);
            if (response.Headers.CacheControl is { } cc) Assert.True(cc.Public, $"{path} should be publicly cacheable");
        }

        var mine = await owner.GetAsync("/api/v1/fantasy/team/squad");
        Assert.True(mine.Headers.CacheControl!.Private);
        Assert.Contains("Authorization", mine.Headers.Vary);

        // The join code is shown to the owner only; a member's league list and the standings never carry it.
        var memberLeagues = await member.GetStringAsync("/api/v1/fantasy/leagues");
        Assert.DoesNotContain(code, memberLeagues);
        var standings = await member.GetStringAsync($"/api/v1/fantasy/leagues/{league.GetProperty("id").GetString()}/standings");
        Assert.DoesNotContain(code, standings);
        Assert.DoesNotContain("@example.com", standings);
        Assert.DoesNotContain("players", standings);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Touchline.Api";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

/// <summary>
/// A full gameweek replay checked through the public API (03 Stage 7): the real GW9 data, the two
/// Sprint 2 reference squads entered as managers, the week settled through the admin routes, and the
/// public standings must show the hand-calculated totals 65 and 61.
/// </summary>
public sealed class GameweekReplayTests(Gw9Fixture f) : IClassFixture<Gw9Fixture>
{
    [SkippableFact]
    public async Task Gw9_settled_through_the_api_publishes_the_reference_totals()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var clock = new TestClock(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));   // after the 48 h hold on the unattributed 62' yellow in 1390
        var tokens = new TestTokens();
        using var api = new TestApiFactory(f.Db.ApiConnectionString, tokens, clock);
        var gw = await f.Db.ScalarAsync<int>("""
            INSERT INTO app.gameweeks (name, start_date, end_date, season_id, deadline_at, number)
            VALUES ('Gameweek 9', '2026-10-26', '2026-11-01', $1, '2026-10-30T16:00:00Z', 9) RETURNING id
            """, f.SeasonId);

        var squads = JsonDocument.Parse(File.ReadAllText(ReferenceData.Path("reference", "gw9-squads.json"))).RootElement.GetProperty("squads").EnumerateArray().ToList();
        var ids = f.Data.GetProperty("players").EnumerateArray().ToDictionary(p => p.GetProperty("sportmonks_player_id").GetInt64(), p => p.GetProperty("id").GetInt32());
        var clients = new List<HttpClient>();
        var teams = new List<int>();
        foreach (var squad in squads)
        {
            var client = api.ClientWithToken(tokens.Create(Guid.NewGuid(), null));
            await Ok(await client.GetAsync("/api/v1/me"));
            var team = (await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = squad.GetProperty("name").GetString()![..12] }))).GetProperty("id").GetInt32();
            // The squad as it stood at the GW9 deadline (picks are entered as the locked entry; the week is in the past).
            var starters = squad.GetProperty("starters").EnumerateArray().Select(x => x.GetInt64()).ToList();
            var bench = squad.GetProperty("bench").EnumerateArray().Select(x => x.GetInt64()).ToList();
            foreach (var sm in starters.Concat(bench))
            {
                short? order = bench.Contains(sm) ? (short)(bench.IndexOf(sm) + 1) : null;
                await f.Db.ExecAsync("""
                    INSERT INTO app.fantasy_squads (fantasy_team_id, player_id, gameweek_id, is_captain, is_vice_captain, is_on_bench, bench_order, purchase_price)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, 6.0)
                    """, team, ids[sm], gw, sm == squad.GetProperty("captain").GetInt64(), sm == squad.GetProperty("vice").GetInt64(), order is not null, order);
            }
            var chip = squad.GetProperty("chip").ValueKind == JsonValueKind.Null ? null : squad.GetProperty("chip").GetString();
            await f.Db.ExecAsync("INSERT INTO app.fantasy_gameweek_entries (fantasy_team_id, gameweek_id, formation, chip, transfers_made, transfer_cost) VALUES ($1, $2, $3, $4, $5, $6)",
                team, gw, squad.GetProperty("formation").GetString(), chip, squad.GetProperty("transferCost").GetInt32() / 4 + 1, squad.GetProperty("transferCost").GetInt32());
            clients.Add(client);
            teams.Add(team);
        }

        var adminClient = api.ClientWithToken(tokens.Create(Guid.NewGuid(), null));
        var admin = await Ok(await adminClient.GetAsync("/api/v1/me"));
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", admin.GetProperty("id").GetInt32());

        var settlement = await Ok(await adminClient.GetAsync($"/api/v1/admin/gameweeks/{gw}/settlement"));
        Assert.Equal("awaiting_review", settlement.GetProperty("state").GetString());   // no worker evidence: never final on states alone
        foreach (var fixture in settlement.GetProperty("fixtures").EnumerateArray())
        {
            var outcome = fixture.GetProperty("unattributedEvents").GetInt32() > 0 ? "unattributed_resolved" : "manual_resolution";
            await Ok(await adminClient.PostAsJsonAsync($"/api/v1/admin/fixtures/{fixture.GetProperty("fixtureId").GetInt32()}/resolution",
                new { outcome, reason = "Checked against the stored GW9 facts" }));
        }
        await Ok(await adminClient.PostAsync($"/api/v1/admin/gameweeks/{gw}/score", null));

        var standings = await Ok(await clients[0].GetAsync("/api/v1/fantasy/leagues/overall/standings"));
        var totals = standings.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("fantasyTeamId").GetInt32(), r => r.GetProperty("overallPoints").GetInt32());
        Assert.Equal(65, totals[teams[0]]);
        Assert.Equal(61, totals[teams[1]]);
        Assert.StartsWith("r", standings.GetProperty("revision").GetString());
    }
}
