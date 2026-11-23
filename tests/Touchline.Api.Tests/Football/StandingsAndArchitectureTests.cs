using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Tests.Db;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Football;

public sealed class StandingsTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task Table_follows_netlify_rules_counts_awarded_results_and_applies_official_deductions()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var c = await f.TeamAsync("Club C", "CCC");
        var d = await f.TeamAsync("Club D", "DDD");
        var t0 = f.Clock.Now.AddDays(-20);
        await f.FixtureAsync(t0, Codes.MatchStates.FullTime, 2, 1);                       // Pirates beat Chiefs
        await f.FixtureAsync(t0.AddDays(1), Codes.MatchStates.FullTime, 1, 1, c, d);      // C 1-1 D
        await f.FixtureAsync(t0.AddDays(2), 17, 3, 0, d, f.Away);                         // awarded 3-0 to D, no events
        await f.FixtureAsync(t0.AddDays(3), Codes.MatchStates.Postponed, null, null, c, f.Home); // not counted
        await f.FixtureAsync(t0.AddDays(4), Codes.MatchStates.SecondHalf, 1, 0, c, f.Away);    // live: not counted
        // Official table: D has a 3-point deduction (same games played, 3 fewer points).
        await f.Db.ExecAsync("INSERT INTO football.standings (season_id, team_id, position, played, points, goal_difference) VALUES ($1, $2, 3, 2, 1, 3)", f.SeasonId, d);

        var response = await f.Api.CreateClient().GetAsync("/api/v1/standings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, json.GetProperty("matchesCounted").GetInt32());
        Assert.Equal(1, json.GetProperty("awardedResultsCounted").GetInt32());

        var rows = json.GetProperty("standings").EnumerateArray().ToDictionary(r => r.GetProperty("teamId").GetInt32());
        Assert.Equal(3, rows[f.Home].GetProperty("points").GetInt32());
        Assert.Equal(1, rows[f.Home].GetProperty("position").GetInt32());
        Assert.Equal(1, rows[d].GetProperty("points").GetInt32());     // 4 earned − 3 deducted
        Assert.Equal(3, rows[d].GetProperty("deduction").GetInt32());
        Assert.Equal(4, rows[d].GetProperty("goalsFor").GetInt32());   // the awarded 3-0 counts by scoreline
        Assert.Equal(0, rows[f.Away].GetProperty("points").GetInt32());
        Assert.Equal(["D", "W"], rows[d].GetProperty("form").EnumerateArray().Select(x => x.GetString()));
        Assert.Null(rows[d].GetProperty("crest").GetString());          // placeholder crest → null
        // C and D level on 1 point: separated by goal difference (C 0, D +3).
        Assert.Equal("goal_difference", rows[c].GetProperty("tieBreak").GetString());
        // No goal events were invented for the awarded match.
        Assert.Equal(0L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM football.match_events"));
    }
}

/// <summary>Architecture rule: Postgres is the only handoff. No Sportmonks or worker access from the API.</summary>
public sealed class ArchitectureTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task Public_reads_make_no_outbound_requests_and_no_provider_or_worker_settings_exist()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddMinutes(-20), Codes.MatchStates.FirstHalf, 0, 0);
        var client = f.Api.CreateClient();
        foreach (var path in new[]
                 {
                     "/api/v1/fixtures", $"/api/v1/fixtures/{fixture}", "/api/v1/standings", "/api/v1/teams",
                     "/api/v1/players", "/api/v1/gameweeks", "/api/v1/seasons/current", "/api/v1/health",
                 })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}");
        }
        Assert.Empty(f.Api.Outbound.Refused);

        var config = f.Api.Services.GetRequiredService<IConfiguration>();
        var keys = config.AsEnumerable().Select(kv => kv.Key).ToList();
        Assert.DoesNotContain(keys, k => Regex.IsMatch(k, "sportmonks.*(token|key|secret|url)|worker.*url|sync.*url", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Source_registers_exactly_one_http_client_the_supabase_jwks_one()
    {
        var src = Path.Combine(TestDatabase.RepoRoot(), "src");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();
        var registrations = files.SelectMany(file => Regex.Matches(File.ReadAllText(file), @"AddHttpClient\(").Select(_ => Path.GetFileName(file))).ToList();
        Assert.Equal(["SupabaseAuthSetup.cs"], registrations);
        Assert.DoesNotContain(files, file => Regex.IsMatch(File.ReadAllText(file), @"new HttpClient\(|WebRequest\.Create|""https?://api\.sportmonks"));
    }
}
