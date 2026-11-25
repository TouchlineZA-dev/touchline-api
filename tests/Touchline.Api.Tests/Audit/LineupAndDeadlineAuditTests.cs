using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>Audit area 3 (substitutions via PUT /lineup) and area 6 (deadlines and locking).</summary>
public sealed class LineupAndDeadlineAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private static Task<HttpResponseMessage> PutLineup(HttpClient client, Pool pool, Spec spec, int? gameweekId = null) =>
        client.PutAsJsonAsync("/api/v1/fantasy/team/lineup", spec.Request(pool, gameweekId ?? pool.NextGameweek));

    private static List<int> BenchOrder(JsonElement squad) => squad.GetProperty("players").EnumerateArray()
        .Where(p => !p.GetProperty("isStarter").GetBoolean()).OrderBy(p => p.GetProperty("benchOrder").GetInt32())
        .Select(p => p.GetProperty("playerId").GetInt32()).ToList();

    // ---- Area 3: substitutions -------------------------------------------------------------------

    [SkippableFact]
    public async Task L01_legal_swaps_save_including_a_formation_change_and_repeat_up_to_the_deadline()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);

        // Bench midfielder for a starting midfielder (4-4-2 stays).
        var swap1 = new Spec("4-4-2",
            ["c0-gk1", "c1-def1", "c2-def1", "c3-def1", "c4-def1", "c13-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c9-att1", "c10-att1"],
            ["c11-gk1", "c12-def1", "c5-mid1", "c14-att1"], "c9-att1", "c10-att1");
        var saved = await Ok(await PutLineup(client, pool, swap1));
        Assert.Contains(saved.GetProperty("players").EnumerateArray(), p => p.GetProperty("playerId").GetInt32() == pool["c13-mid1"] && p.GetProperty("isStarter").GetBoolean());

        // Defender out, midfielder in: 3-5-2.
        var swap2 = new Spec("3-5-2",
            ["c0-gk1", "c1-def1", "c2-def1", "c3-def1", "c13-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c5-mid1", "c9-att1", "c10-att1"],
            ["c11-gk1", "c12-def1", "c4-def1", "c14-att1"], "c9-att1", "c10-att1");
        saved = await Ok(await PutLineup(client, pool, swap2));
        Assert.Equal("3-5-2", saved.GetProperty("formation").GetString());

        // And back again: repeated swaps are fine before the deadline.
        await Ok(await PutLineup(client, pool, Cheap()));
    }

    [SkippableFact]
    public async Task L02_swaps_that_break_the_formation_or_move_the_bench_goalkeeper_are_rejected()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);

        // Starting from 3-5-2, a defender for the bench attacker leaves 2 defenders.
        var twoDefenders = new Spec("2-5-3",
            ["c0-gk1", "c1-def1", "c2-def1", "c5-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c13-mid1", "c9-att1", "c10-att1", "c14-att1"],
            ["c11-gk1", "c3-def1", "c4-def1", "c12-def1"], "c9-att1", "c10-att1");
        var r = await Send(PutLineup(client, pool, twoDefenders));
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Contains(r.Body.GetProperty("violations").EnumerateArray(), v => v.GetProperty("rule").GetString() is "formation_range" or "formation_not_allowed");

        // The bench goalkeeper into midfield.
        var gkInMidfield = new Spec("4-4-2",
            ["c0-gk1", "c1-def1", "c2-def1", "c3-def1", "c4-def1", "c11-gk1", "c6-mid1", "c7-mid1", "c8-mid1", "c9-att1", "c10-att1"],
            ["c5-mid1", "c12-def1", "c13-mid1", "c14-att1"], "c9-att1", "c10-att1");
        AssertError(await Send(PutLineup(client, pool, gkInMidfield)), HttpStatusCode.BadRequest, "rule_violation", "bench_gk_swap");

        // Goalkeeper for goalkeeper is fine.
        await Ok(await PutLineup(client, pool, Cheap().Swap("c0-gk1", "TMP").Swap("c11-gk1", "c0-gk1").Swap("TMP", "c11-gk1")));

        // Bringing in a player who isn't in the squad needs a transfer.
        AssertError(await Send(PutLineup(client, pool, Cheap().Swap("c14-att1", "c15-att1"))), HttpStatusCode.BadRequest, "rule_violation", "lineup_changes_players");
    }

    [SkippableFact]
    public async Task L03_bench_order_is_saved_and_captaincy_is_revalidated_when_the_captain_moves_to_the_bench()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var reordered = Cheap() with { Bench = ["c11-gk1", "c14-att1", "c13-mid1", "c12-def1"] };
        var saved = await Ok(await PutLineup(client, pool, reordered));
        Assert.Equal(reordered.Bench.Select(k => pool[k]).ToList(), BenchOrder(saved));
        Assert.Equal(reordered.Bench.Select(k => pool[k]).ToList(), BenchOrder(await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"))));

        // Captain benched (swapped with the bench attacker) but still named captain: rejected.
        var captainBenched = Cheap().Swap("c9-att1", "TMP").Swap("c14-att1", "c9-att1").Swap("TMP", "c14-att1") with { Captain = "c9-att1" };
        AssertError(await Send(PutLineup(client, pool, captainBenched)), HttpStatusCode.BadRequest, "rule_violation", "captain_not_starter");
    }

    [SkippableFact]
    public async Task L04_after_the_deadline_a_lineup_change_is_409_and_nothing_changes()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var before = await Ok(await client.GetAsync($"/api/v1/fantasy/team/squad?gameweek={await NumberAsync(pool.NextGameweek)}"));

        await AtAsync(f, pool.NextDeadline.AddMinutes(1), async () =>
        {
            var swap = Cheap().Swap("c5-mid1", "TMP").Swap("c13-mid1", "c5-mid1").Swap("TMP", "c13-mid1");
            var r = await Send(PutLineup(client, pool, swap, pool.NextGameweek));
            AssertError(r, HttpStatusCode.Conflict, "gameweek_locked");
            Assert.Equal(pool.LaterGameweek, r.Body.GetProperty("current").GetProperty("nextGameweek").GetProperty("id").GetInt32());

            // The locked week is exactly as it was, and the next week wasn't silently edited either.
            var locked = await Ok(await client.GetAsync($"/api/v1/fantasy/team/squad?gameweek={await NumberAsync(pool.NextGameweek)}"));
            Assert.Equal(Starters(before), Starters(locked));
            var nextWeek = await Ok(await client.GetAsync($"/api/v1/fantasy/team/squad?gameweek={await NumberAsync(pool.LaterGameweek)}"));
            Assert.Equal(Starters(before), Starters(nextWeek));
        });
    }

    private static List<int> Starters(JsonElement squad) => squad.GetProperty("players").EnumerateArray()
        .Where(p => p.GetProperty("isStarter").GetBoolean()).Select(p => p.GetProperty("playerId").GetInt32()).Order().ToList();

    private Task<short> NumberAsync(int gameweekId) => f.Db.ScalarAsync<short>("SELECT number FROM app.gameweeks WHERE id = $1", gameweekId);

    // ---- Area 6: deadlines and locking ---------------------------------------------------------

    [SkippableFact]
    public async Task D01_without_a_stored_deadline_it_is_90_minutes_before_the_first_kickoff()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var start = DateOnly.FromDateTime(f.Clock.Now.AddDays(40).UtcDateTime);
        var gw = await f.Db.ScalarAsync<int>("""
            INSERT INTO app.gameweeks (name, start_date, end_date, season_id, number)
            VALUES ('Audit GW derived', $1, $2, $3, (SELECT COALESCE(MAX(number), 0) + 1 FROM app.gameweeks WHERE season_id = $3)) RETURNING id
            """, start, start.AddDays(6), f.SeasonId);
        var first = new DateTimeOffset(start.ToDateTime(new TimeOnly(13, 0)), TimeSpan.Zero);   // 15:00 SAST
        await f.FixtureAsync(first, 1);
        await f.FixtureAsync(first.AddHours(3), 1);
        // Both fixtures fall inside the gameweek's SAST dates; the API assigns them by date (no stored deadline).

        var list = await Ok(await f.Api.CreateClient().GetAsync($"/api/v1/gameweeks?seasonId={f.SeasonId}"));
        var row = list.GetProperty("gameweeks").EnumerateArray().Single(g => g.GetProperty("id").GetInt32() == gw);
        Assert.Equal(first.AddMinutes(-90), row.GetProperty("deadline").GetDateTimeOffset());
    }

    [SkippableFact]
    public async Task D02_a_write_aimed_at_a_locked_gameweek_never_edits_the_next_one()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        await AtAsync(f, pool.NextDeadline.AddMinutes(1), async () =>
        {
            var chip = await Send(client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "bench_boost", gameweekId = pool.NextGameweek }));
            AssertError(chip, HttpStatusCode.Conflict, "gameweek_locked");
            var transfers = await Send(client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview", new { gameweekId = pool.NextGameweek, @out = new[] { pool["c14-att1"] }, @in = new[] { pool["c15-att1"] } }));
            AssertError(transfers, HttpStatusCode.Conflict, "gameweek_locked");
            var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
            Assert.Equal(0L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_chip_usage WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));
            Assert.Equal(0L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_transfers WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));
        });
    }

    [SkippableFact]
    public async Task D03_rivals_cannot_see_picks_through_league_routes()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var owner = await ManagerWithSquadAsync(f, pool);
        var league = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Audit Privacy" }));
        var rival = await ManagerWithSquadAsync(f, pool);
        await Ok(await rival.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code = league.GetProperty("joinCode").GetString() }));

        var standings = (await Ok(await rival.GetAsync($"/api/v1/fantasy/leagues/{league.GetProperty("id").GetString()}/standings"))).ToString();
        foreach (var key in Cheap().Starters.Concat(Cheap().Bench))
        {
            Assert.DoesNotContain($"\"playerId\":{pool[key]}", standings);
        }
        Assert.DoesNotContain("\"picks\"", standings);
        Assert.DoesNotContain("\"players\"", standings);
    }

    [SkippableFact]
    public async Task D04_a_late_entrant_starts_at_the_next_open_gameweek_with_unlimited_free_transfers_and_no_back_points()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        await AtAsync(f, pool.NextDeadline.AddMinutes(1), async () =>
        {
            var client = await ManagerWithTeamAsync(f, "Late Entry");
            // The first squad goes to the open week (the later one); naming the locked week is refused.
            AssertError(await Send(client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Cheap().Request(pool, pool.NextGameweek))),
                HttpStatusCode.Conflict, "gameweek_locked");
            var squad = await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Cheap().Request(pool, pool.LaterGameweek)));
            Assert.Equal(pool.LaterGameweek, squad.GetProperty("gameweek").GetProperty("id").GetInt32());

            var quote = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview", new
            {
                gameweekId = pool.LaterGameweek,
                @out = new[] { pool["c9-att1"], pool["c10-att1"], pool["c14-att1"] },
                @in = new[] { pool["c15-att1"], pool["c15-att2"], pool["c13-att1"] },
            }));
            Assert.Equal(JsonValueKind.Null, quote.GetProperty("freeTransfersBefore").ValueKind);   // unlimited
            Assert.Equal(0, quote.GetProperty("pointsHit").GetInt32());

            // No entry, and no points, for the gameweek before the team existed.
            var history = await Ok(await client.GetAsync("/api/v1/fantasy/team/history"));
            Assert.DoesNotContain($"\"gameweekId\":{pool.NextGameweek}", history.ToString());
            Assert.Equal(0L, await f.Db.ScalarAsync<long>("""
                SELECT count(*) FROM app.fantasy_gameweek_entries e JOIN app.fantasy_teams t ON t.id = e.fantasy_team_id
                WHERE t.name = 'Late Entry' AND e.gameweek_id = $1
                """, pool.NextGameweek));
        });
    }
}
