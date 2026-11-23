using System.Net;
using System.Text.Json;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Reference;

namespace Touchline.Api.Tests.Contract;

/// <summary>Player detail, club detail, player rankings and the /matches alias over the real GW9 snapshot.</summary>
public sealed class FootballContractTests(Gw9Fixture f) : IClassFixture<Gw9Fixture>
{
    private async Task<JsonElement> Get(string path)
    {
        var response = await f.Api.CreateClient().GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private int IdOf(long sportmonksId) => f.Data.GetProperty("players").EnumerateArray()
        .Single(p => p.GetProperty("sportmonks_player_id").GetInt64() == sportmonksId).GetProperty("id").GetInt32();

    [SkippableFact]
    public async Task Player_detail_has_identity_club_eligibility_and_recent_matches_with_points_reasons()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var mokobodi = await Get($"/api/v1/players/{IdOf(37723175)}");
        Assert.Equal("MID", mokobodi.GetProperty("position").GetString());
        Assert.Equal(50, mokobodi.GetProperty("clubId").GetInt32());
        Assert.True(mokobodi.GetProperty("available").GetBoolean());
        Assert.Equal("current_season_lineup", mokobodi.GetProperty("availabilitySource").GetString());
        var match = mokobodi.GetProperty("recentMatches").EnumerateArray().Single(m => m.GetProperty("fixtureId").GetInt32() == 1393);
        Assert.Equal(6, match.GetProperty("points").GetInt32());   // full match 2 + two assists 4
        Assert.Equal(50, match.GetProperty("clubId").GetInt32());  // the club he played for in that match
        Assert.Contains(match.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.StartsWith("2 assist(s)"));

        Assert.Equal(HttpStatusCode.NotFound, (await f.Api.CreateClient().GetAsync("/api/v1/players/99999999")).StatusCode);
    }

    [SkippableFact]
    public async Task Club_detail_has_the_table_row_fixtures_and_squad()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pirates = await Get("/api/v1/clubs/50");
        Assert.Equal("Orlando Pirates", pirates.GetProperty("club").GetProperty("name").GetString());
        Assert.Equal(3, pirates.GetProperty("standing").GetProperty("points").GetInt32());
        Assert.Contains(pirates.GetProperty("fixtures").EnumerateArray(), x => x.GetProperty("id").GetInt32() == 1393);
        Assert.Contains(pirates.GetProperty("squad").EnumerateArray(), p => p.GetProperty("id").GetInt32() == IdOf(37723175));
    }

    [SkippableFact]
    public async Task Player_rankings_keep_the_netlify_rules_and_matches_alias_serves_match_detail()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var rankings = await Get("/api/v1/player-rankings");
        Assert.Equal(8, rankings.GetProperty("matchesCounted").GetInt32());
        var goals = rankings.GetProperty("rankings").GetProperty("goals").EnumerateArray().ToList();
        Assert.Equal(2, goals[0].GetProperty("total").GetInt32());
        Assert.Contains(goals.Take(3), g => g.GetProperty("player").GetString() == "Thabang Matuludi");
        Assert.Contains(rankings.GetProperty("rankings").GetProperty("assists").EnumerateArray(),
            a => a.GetProperty("player").GetString() == "Thapelo Mokobodi" && a.GetProperty("total").GetInt32() == 2);
        Assert.True(goals.Count <= 10);

        var alias = await Get("/api/v1/matches?fixtureId=1393");
        var detail = await Get("/api/v1/fixtures/1393");
        Assert.Equal(detail.GetProperty("revision").GetString(), alias.GetProperty("revision").GetString());
    }

    [SkippableFact]
    public async Task Fixture_list_serves_kickoff_alongside_kickoffUtc_and_pages_on_request()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var all = await Get("/api/v1/fixtures");
        var first = all.GetProperty("fixtures")[0];
        Assert.Equal(first.GetProperty("kickoffUtc").GetDateTimeOffset(), first.GetProperty("kickoff").GetDateTimeOffset());
        var page = await Get("/api/v1/fixtures?page=2&pageSize=3");
        Assert.Equal(3, page.GetProperty("fixtures").GetArrayLength());
        Assert.Equal(8, page.GetProperty("paging").GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task Health_is_split_into_minimal_liveness_readiness_and_freshness()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var health = await Get("/api/v1/health");
        Assert.Equal(["status", "checkedAtUtc"], health.EnumerateObject().Select(p => p.Name));
        Assert.True((await Get("/api/v1/health/ready")).GetProperty("ready").GetBoolean());
        var freshness = await Get("/api/v1/health/freshness");
        Assert.True(freshness.TryGetProperty("lastFootballCommitUtc", out _));
        Assert.True(freshness.TryGetProperty("fresh", out _));
    }

    [SkippableFact]
    public async Task Cors_exposes_the_etag_to_allowed_origins_only()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        using var api = new TestApiFactory(f.Db.ApiConnectionString, new TestTokens(), null,
            new Dictionary<string, string> { ["Cors:AllowedOrigins:0"] = "https://app.touchline.test" });
        var client = api.CreateClient();
        var allowed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clubs");
        allowed.Headers.Add("Origin", "https://app.touchline.test");
        var response = await client.SendAsync(allowed);
        Assert.Equal("https://app.touchline.test", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("ETag", response.Headers.GetValues("Access-Control-Expose-Headers").Single());

        var other = new HttpRequestMessage(HttpMethod.Get, "/api/v1/clubs");
        other.Headers.Add("Origin", "https://evil.example");
        Assert.False((await client.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin"));
    }
}
