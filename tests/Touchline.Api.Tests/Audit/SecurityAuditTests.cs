using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>
/// Audit area 9: every signed-in route refuses missing, invalid and expired tokens with a reason the app can act
/// on (token_expired = refresh and retry; token_missing / token_invalid = sign in), other errors never look like a
/// sign-out, and one manager can never reach another's team, drafts or private league.
/// </summary>
public sealed class SecurityAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    /// <summary>Every route that needs a token, as (method, concrete path), from the app's own route table.</summary>
    /// <summary>
    /// Its own app instance with rate limits out of the way: this test sends ~3 requests to every route, and the
    /// per-minute limits (which answer 429 before the token is checked) would otherwise stop it.
    /// </summary>
    private TestApiFactory Unlimited() => new(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string>
    {
        ["RateLimits:AccountChangesPerMinute"] = "100000", ["RateLimits:FantasyWritesPerMinute"] = "100000",
        ["RateLimits:StandingsPerMinute"] = "100000", ["RateLimits:AnonymousPerMinute"] = "100000",
        ["RateLimits:DraftSavesPerMinute"] = "100000", ["RateLimits:WizardChecksPerMinute"] = "100000",
    });

    private static List<(HttpMethod Method, string Path)> SignedInRoutes(TestApiFactory api)
    {
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var list = new List<(HttpMethod, string)>();
        foreach (var e in endpoints)
        {
            if (e.Metadata.GetMetadata<IAuthorizeData>() is null || e.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;
            var raw = "/" + e.RoutePattern.RawText!;
            var path = raw.Replace("{id:int}", "1").Replace("{id}", "1").Replace("{kind}", "squad").Replace("{chip}", "bench_boost");
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                list.Add((new HttpMethod(method), path));
            }
        }
        return list;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get && method != HttpMethod.Delete) request.Content = JsonContent.Create(new { });
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    [SkippableFact]
    public async Task X01_every_signed_in_route_answers_401_with_a_reason_for_missing_invalid_and_expired_tokens()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        using var api = Unlimited();
        var routes = SignedInRoutes(api);
        Assert.True(routes.Count >= 50, $"only {routes.Count} signed-in routes found");
        Assert.Contains(routes, r => r.Path == "/api/v1/me/drafts/squad");
        Assert.Contains(routes, r => r.Path == "/api/v1/fantasy/team/squad/validate");

        var client = api.CreateClient();
        var otherKey = TestTokens.NewKey("not-published");
        var invalid = f.Tokens.Create(Guid.NewGuid(), null, signWith: otherKey);
        var expired = f.Tokens.Create(Guid.NewGuid(), null, expires: DateTime.UtcNow.AddMinutes(-10));   // beyond the 5-minute clock skew

        foreach (var (method, path) in routes)
        {
            foreach (var (token, code, retry) in new[] { ((string?)null, "token_missing", "sign_in"), (invalid, "token_invalid", "sign_in"), (expired, "token_expired", "refresh") })
            {
                var response = await client.SendAsync(Request(method, path, token));
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} with {code}: {(int)response.StatusCode} {body}");
                var json = JsonDocument.Parse(body).RootElement;
                Assert.Equal(code, json.GetProperty("code").GetString());
                Assert.Equal(retry, json.GetProperty("retry").GetString());
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
                var header = response.Headers.WwwAuthenticate.ToString();
                if (token is null) Assert.Equal("Bearer", header);
                else Assert.Contains("error=\"invalid_token\"", header);
                if (code == "token_expired") Assert.Contains("expired", header);
            }
        }
    }

    [SkippableFact]
    public async Task X02_validation_conflict_and_forbidden_errors_are_never_401_and_never_ask_to_sign_in()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "free_hit", gameweekId = pool.NextGameweek }),        // 400
            await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Cheap().Request(pool, pool.NextGameweek)),                         // 409
            await client.GetAsync("/api/v1/admin/diagnostics"),                                                                           // 403
            await client.GetAsync("/api/v1/fantasy/leagues/999999/standings"),                                                            // 403/404
        };
        foreach (var r in responses)
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Empty(r.Headers.WwwAuthenticate);
            var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.True(json.TryGetProperty("code", out var code) && code.GetString() is { Length: > 0 }, json.ToString());
            Assert.False(json.TryGetProperty("retry", out _));
        }
    }

    [SkippableFact]
    public async Task X03_non_admins_get_403_on_admin_routes()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithTeamAsync(f);
        foreach (var r in new[]
                 {
                     await client.GetAsync("/api/v1/admin/diagnostics"),
                     await client.PostAsJsonAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/score", new { }),
                     await client.PutAsJsonAsync($"/api/v1/admin/players/{pool["x-mid"]}/available", new { available = true, reason = "no" }),
                     await client.PostAsJsonAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/lock", new { }),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden", JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
    }

    [SkippableFact]
    public async Task X04_one_manager_can_never_read_or_change_anothers_team_drafts_or_private_league()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var a = await ManagerWithSquadAsync(f, pool);
        var b = await ManagerWithTeamAsync(f, "Team B");
        var teamA = await Ok(await a.GetAsync("/api/v1/fantasy/team"));
        var teamB = await Ok(await b.GetAsync("/api/v1/fantasy/team"));
        Assert.NotEqual(teamA.GetProperty("id").GetInt32(), teamB.GetProperty("id").GetInt32());

        // B's squad routes only ever see B's (empty) squad, whatever is sent.
        var bSquad = await Ok(await b.GetAsync("/api/v1/fantasy/team/squad"));
        Assert.Equal(teamB.GetProperty("id").GetInt32(), bSquad.GetProperty("teamId").GetInt32());
        Assert.Empty(bSquad.GetProperty("players").EnumerateArray());

        // Drafts are per manager.
        await Ok(await a.PutAsJsonAsync("/api/v1/me/drafts/transfers", new { payload = new { secret = "a" }, version = 0 }));
        AssertError(await Send(b.GetAsync("/api/v1/me/drafts/transfers")), HttpStatusCode.NotFound, "draft_not_found");
        Assert.DoesNotContain("transfers", (await Ok(await b.GetAsync("/api/v1/me/drafts"))).ToString());

        // A's private league: B can't read it or take it over.
        var league = await Ok(await a.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "A only" }));
        var id = league.GetProperty("id").GetString();
        AssertError(await Send(b.GetAsync($"/api/v1/fantasy/leagues/{id}/standings")), HttpStatusCode.Forbidden, "forbidden");
        var takeover = await Send(b.PostAsJsonAsync($"/api/v1/fantasy/leagues/{id}/owner", new { fantasyTeamId = teamB.GetProperty("id").GetInt32() }));
        Assert.True(takeover.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, takeover.Body.ToString());
        var archive = await Send(b.PostAsync($"/api/v1/fantasy/leagues/{id}/archive", null));
        Assert.True(archive.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, archive.Body.ToString());
    }
}
