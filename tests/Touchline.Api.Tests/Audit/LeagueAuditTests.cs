using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Touchline.Api.Fantasy;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>Audit area 8: private leagues, the Overall league, standings and tie-breaks.</summary>
public sealed partial class LeagueAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [GeneratedRegex(@"^TL-(?![#!@-])(?:\d|[#!@-](?![#!@-])){8}(?<![#!@-])$")]
    private static partial Regex CodeShape();

    private static Task<HttpResponseMessage> Join(HttpClient c, string code) => c.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code });

    [SkippableFact]
    public async Task G01_codes_are_TL_plus_6_digits_with_2_specials_between_digits_and_joining_is_by_exact_code_only()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var owner = await ManagerWithTeamAsync(f);
        for (var i = 0; i < 5; i++)
        {
            var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = $"Code shape {i}" }));
            var code = league.GetProperty("joinCode").GetString()!;
            Assert.Matches(CodeShape(), code);
            Assert.Equal(6, code.Count(char.IsAsciiDigit));
            Assert.Equal(2, code[3..].Count(ch => "#!@-".Contains(ch)));
        }

        var joiner = await ManagerWithTeamAsync(f);
        AssertError(await Send(Join(joiner, "TL-12345")), HttpStatusCode.BadRequest, "rule_violation", "league_code_format");
        AssertError(await Send(Join(joiner, "TL-1#2345!6")), HttpStatusCode.NotFound, "not_found");   // well formed, no such league
        // There is no public list of private leagues: the only way in is the code.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await joiner.GetAsync("/api/v1/fantasy/leagues/join")).StatusCode);
    }

    [SkippableFact]
    public async Task G02_a_league_holds_20_the_21st_is_refused_also_when_two_join_at_once_for_the_last_place()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var owner = await ManagerWithTeamAsync(f);
        var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Twenty" }));
        var code = league.GetProperty("joinCode").GetString()!;
        for (var i = 0; i < 18; i++) await Ok(await Join(await ManagerWithTeamAsync(f, $"Member {i}"), code));   // owner + 18 = 19

        var a = await ManagerWithTeamAsync(f, "Racer A");
        var b = await ManagerWithTeamAsync(f, "Racer B");
        var results = await Task.WhenAll(Join(a, code), Join(b, code));
        Assert.Single(results, r => r.IsSuccessStatusCode);
        var loser = results.Single(r => !r.IsSuccessStatusCode);
        var body = JsonDocument.Parse(await loser.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("league_full", body.GetProperty("code").GetString());

        AssertError(await Send(Join(await ManagerWithTeamAsync(f, "Number 21"), code)), HttpStatusCode.Conflict, "league_full");
        Assert.Equal(20L, await Touchline.Api.Tests.Db.TestDatabase.ScalarAsync<long>(f.Db,
            "SELECT count(*) FROM app.league_members WHERE league_id = $1 AND left_at IS NULL", int.Parse(league.GetProperty("id").GetString()!)));
    }

    [SkippableFact]
    public async Task G03_the_owner_cannot_leave_members_can_leave_and_rejoin_and_overall_always_exists()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var owner = await ManagerWithTeamAsync(f);
        var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Leave Rejoin" }));
        var id = league.GetProperty("id").GetString();
        AssertError(await Send(owner.PostAsync($"/api/v1/fantasy/leagues/{id}/leave", null)), HttpStatusCode.BadRequest, "rule_violation", "owner_cannot_leave");

        var member = await ManagerWithTeamAsync(f);
        await Ok(await Join(member, league.GetProperty("joinCode").GetString()!));
        AssertError(await Send(Join(member, league.GetProperty("joinCode").GetString()!)), HttpStatusCode.Conflict, "already_member");
        await Ok(await member.PostAsync($"/api/v1/fantasy/leagues/{id}/leave", null));
        await Ok(await Join(member, league.GetProperty("joinCode").GetString()!));

        var mine = await Ok(await member.GetAsync("/api/v1/fantasy/leagues"));
        var list = mine.ValueKind == JsonValueKind.Array ? mine : mine.GetProperty("leagues");
        Assert.Contains(list.EnumerateArray(), l => l.GetProperty("isOverall").GetBoolean());
        Assert.Contains(list.EnumerateArray(), l => l.GetProperty("id").GetString() == id);
        await Ok(await member.GetAsync("/api/v1/fantasy/leagues/overall/standings"));
    }

    [SkippableFact]
    public async Task G04_a_non_member_cannot_read_a_private_leagues_standings()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var owner = await ManagerWithTeamAsync(f);
        var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Private" }));
        var outsider = await ManagerWithTeamAsync(f);
        var r = await Send(outsider.GetAsync($"/api/v1/fantasy/leagues/{league.GetProperty("id").GetString()}/standings"));
        AssertError(r, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public void G05_ties_go_to_overall_points_then_username_then_team_id()
    {
        var ranked = LeagueRules.Rank(
        [
            new StandingInput(3, "C", "carol", 50, 300, false),
            new StandingInput(1, "A", "alice", 50, 310, false),
            new StandingInput(2, "B", "Bob", 50, 300, false),
            new StandingInput(5, "E", "bob", 50, 300, false),
            new StandingInput(4, "D", "dave", 60, 200, false),
        ]);
        Assert.Equal([4, 1, 2, 5, 3], ranked.Select(r => r.Entry.FantasyTeamId));
        Assert.Equal([1, 2, 3, 4, 5], ranked.Select(r => r.Rank));
    }

    [SkippableFact]
    public async Task G06_league_ranks_use_the_same_points_as_the_points_page()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var a = await ManagerWithSquadAsync(f, pool);                                        // captain c9-att1
        var b = await ManagerWithSquadAsync(f, pool, Cheap() with { Captain = "c10-att1", Vice = "c9-att1" });
        var league = await Ok(await a.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Same Points" }));
        await Ok(await b.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code = league.GetProperty("joinCode").GetString() }));

        // A finished match inside the open gameweek: c9-att1 scores, c10-att1 plays.
        var kickoff = pool.NextDeadline.AddHours(2);
        var fixture = await f.FixtureAsync(kickoff, 5, 1, 0, pool.Clubs[9], pool.Clubs[10]);
        await f.LineupAsync(fixture, pool.Clubs[9], pool.Sm["c9-att1"], 11);
        await f.LineupAsync(fixture, pool.Clubs[10], pool.Sm["c10-att1"], 11);
        await f.EventAsync(fixture, FootballDbFixture.NextId(), 14, pool.Clubs[9], pool.Sm["c9-att1"], null, 30);
        var number = await Touchline.Api.Tests.Db.TestDatabase.ScalarAsync<short>(f.Db, "SELECT number FROM app.gameweeks WHERE id = $1", pool.NextGameweek);

        await AtAsync(f, kickoff.AddHours(3), async () =>
        {
            var pointsA = await Ok(await a.GetAsync($"/api/v1/fantasy/team/points?gameweek={number}"));
            var pointsB = await Ok(await b.GetAsync($"/api/v1/fantasy/team/points?gameweek={number}"));
            Assert.NotEqual(pointsA.GetProperty("total").GetInt32(), pointsB.GetProperty("total").GetInt32());   // different captains

            var week = await Ok(await a.GetAsync($"/api/v1/fantasy/leagues/{league.GetProperty("id").GetString()}/standings?gameweek={number}"));
            var overall = await Ok(await a.GetAsync($"/api/v1/fantasy/leagues/{league.GetProperty("id").GetString()}/standings"));
            foreach (var (points, client) in new[] { (pointsA, a), (pointsB, b) })
            {
                var teamId = points.GetProperty("teamId").GetInt32();
                var row = week.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("fantasyTeamId").GetInt32() == teamId);
                Assert.Equal(points.GetProperty("total").GetInt32(), row.GetProperty("points").GetInt32());
                var overallRow = overall.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("fantasyTeamId").GetInt32() == teamId);
                Assert.Equal(points.GetProperty("total").GetInt32(), overallRow.GetProperty("points").GetInt32());
            }
            // The higher total ranks first.
            var leader = week.GetProperty("rows")[0].GetProperty("fantasyTeamId").GetInt32();
            var best = pointsA.GetProperty("total").GetInt32() > pointsB.GetProperty("total").GetInt32() ? pointsA : pointsB;
            Assert.Equal(best.GetProperty("teamId").GetInt32(), leader);
            Assert.EndsWith("+live", week.GetProperty("revision").GetString());   // provisional on both pages
            Assert.True(pointsA.GetProperty("provisional").GetBoolean());
        });
    }
}
