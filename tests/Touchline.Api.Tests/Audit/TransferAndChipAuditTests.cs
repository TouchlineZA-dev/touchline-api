using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Fantasy;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>Audit area 4 (transfers) and area 5 (chips).</summary>
public sealed class TransferAndChipAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private static object Ids(Pool pool, int gameweekId, string[] outKeys, string[] inKeys) =>
        new { gameweekId, @out = outKeys.Select(k => pool[k]).ToArray(), @in = inKeys.Select(k => pool[k]).ToArray() };

    private static async Task<JsonElement> Preview(HttpClient client, Pool pool, int gw, string[] outKeys, string[] inKeys) =>
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview", Ids(pool, gw, outKeys, inKeys)));

    private static HttpRequestMessage Confirm(JsonElement quote, string key) => Json(HttpMethod.Post, "/api/v1/fantasy/team/transfers/confirm", new
    {
        gameweekId = quote.GetProperty("gameweekId").GetInt32(),
        @out = quote.GetProperty("out").EnumerateArray().Select(p => p.GetProperty("playerId").GetInt32()).ToArray(),
        @in = quote.GetProperty("in").EnumerateArray().Select(p => p.GetProperty("playerId").GetInt32()).ToArray(),
        squadVersion = quote.GetProperty("squadVersion").GetInt32(),
        priceRevision = quote.GetProperty("priceRevision").GetInt32(),
        quotedAtUtc = quote.GetProperty("quotedAtUtc").GetDateTimeOffset(),
        saleProceeds = quote.GetProperty("saleProceeds").GetDecimal(),
        purchaseCost = quote.GetProperty("purchaseCost").GetDecimal(),
    }, ("Idempotency-Key", key));

    /// <summary>Runs the body in the week after the team's first, where the normal allowance applies.</summary>
    private Task InSecondWeek(Pool pool, Func<Task> body) => AtAsync(f, pool.NextDeadline.AddMinutes(1), body);

    // ---- Area 4: transfers -----------------------------------------------------------------------

    [SkippableFact]
    public async Task T01_one_free_transfer_per_gameweek_then_minus_4_each_and_no_rollover()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        await InSecondWeek(pool, async () =>
        {
            var one = await Preview(client, pool, pool.LaterGameweek, ["c9-att1"], ["c15-att1"]);
            Assert.Equal(1, one.GetProperty("freeTransfersBefore").GetInt32());
            Assert.Equal(0, one.GetProperty("pointsHit").GetInt32());
            var three = await Preview(client, pool, pool.LaterGameweek, ["c9-att1", "c10-att1", "c14-att1"], ["c15-att1", "c15-att2", "c13-att1"]);
            Assert.Equal(8, three.GetProperty("pointsHit").GetInt32());   // 1 free, 2 × −4

            await Ok(await client.SendAsync(Confirm(one, Guid.NewGuid().ToString())));
            var second = await Preview(client, pool, pool.LaterGameweek, ["c10-att1"], ["c15-att2"]);
            Assert.Equal(0, second.GetProperty("freeTransfersBefore").GetInt32());
            Assert.Equal(4, second.GetProperty("pointsHit").GetInt32());
        });
        // A week with no transfers still gives only 1 free transfer the week after (no rollover).
        var fresh = await ManagerWithSquadAsync(f, pool);
        await AtAsync(f, pool.LaterDeadline.AddMinutes(1), async () =>
        {
            var q = await Preview(fresh, pool, pool.FourthGameweek, ["c9-att1", "c10-att1"], ["c15-att1", "c15-att2"]);
            Assert.Equal(1, q.GetProperty("freeTransfersBefore").GetInt32());
            Assert.Equal(4, q.GetProperty("pointsHit").GetInt32());
        });
    }

    [SkippableFact]
    public async Task T02_transfers_are_unlimited_and_free_in_the_first_week_and_with_wildcard()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var first = await Preview(client, pool, pool.NextGameweek, ["c9-att1", "c10-att1", "c14-att1"], ["c15-att1", "c15-att2", "c13-att1"]);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("freeTransfersBefore").ValueKind);
        Assert.Equal(0, first.GetProperty("pointsHit").GetInt32());

        await InSecondWeek(pool, async () =>
        {
            await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "wildcard", gameweekId = pool.LaterGameweek }));
            var wild = await Preview(client, pool, pool.LaterGameweek, ["c9-att1", "c10-att1", "c14-att1"], ["c15-att1", "c15-att2", "c13-att1"]);
            Assert.Equal(JsonValueKind.Null, wild.GetProperty("freeTransfersBefore").ValueKind);
            Assert.Equal(0, wild.GetProperty("pointsHit").GetInt32());
        });
    }

    [SkippableFact]
    public async Task T03_transfers_are_like_for_like_and_recheck_the_club_limit_and_the_bank()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var wrongPosition = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview", Ids(pool, pool.NextGameweek, ["c1-def1"], ["c15-mid1"])));
        Assert.False(wrongPosition.GetProperty("valid").GetBoolean());

        var confirmWrong = await Send(client.SendAsync(Confirm(wrongPosition, Guid.NewGuid().ToString())));
        AssertError(confirmWrong, HttpStatusCode.BadRequest, "rule_violation");

        // Club 1 already has c1-def1; two more from club 1 is three (fine), a third more is four.
        var fourth = await Preview(client, pool, pool.NextGameweek, ["c2-def1", "c3-def1", "c4-def1"], ["c1-def2", "c1-def3", "c1-def4"]);
        Assert.False(fourth.GetProperty("valid").GetBoolean());
        Assert.Contains(fourth.GetProperty("violations").EnumerateArray(), v => v.GetString()!.Contains("Max 3 players"));

        // Exactly R100.0m squad: any upgrade overspends.
        var rich = await ManagerWithSquadAsync(f, pool, Exactly100());
        var broke = await Preview(rich, pool, pool.NextGameweek, ["c8-mid1"], ["c15-mid2"]);
        Assert.False(broke.GetProperty("valid").GetBoolean());
        AssertError(await Send(rich.SendAsync(Confirm(broke, Guid.NewGuid().ToString()))), HttpStatusCode.BadRequest, "insufficient_funds");
    }

    [SkippableFact]
    public async Task T04_sell_and_buy_at_current_prices_the_bank_balances_and_the_quote_equals_the_result()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var spec = Cheap().Swap("c14-att1", "c15-att2");   // bench attacker bought at 7.5
        var client = await ManagerWithSquadAsync(f, pool, spec);
        var bankBefore = (await Ok(await client.GetAsync("/api/v1/fantasy/team"))).GetProperty("budgetRemaining").GetDecimal();
        Assert.Equal(100.0m - spec.Cost(pool), bankBefore);

        // His price rises to 8.0 after purchase: selling returns the current price.
        await f.Db.ExecAsync("UPDATE app.player_prices SET price = 8.0 WHERE player_id = $1 AND season_id = $2", pool["c15-att2"], f.SeasonId);
        await f.Db.ExecAsync("INSERT INTO app.player_price_changes (player_id, season_id, gameweek_id, old_price, new_price) VALUES ($1, $2, $3, 7.5, 8.0)",
            pool["c15-att2"], f.SeasonId, pool.NextGameweek);

        var quote = await Preview(client, pool, pool.NextGameweek, ["c15-att2"], ["c13-att3"]);
        Assert.Equal(8.0m, quote.GetProperty("saleProceeds").GetDecimal());
        Assert.Equal(10.5m, quote.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(bankBefore + 8.0m - 10.5m, quote.GetProperty("bankAfter").GetDecimal());

        var receipt = await Ok(await client.SendAsync(Confirm(quote, Guid.NewGuid().ToString())));
        Assert.Equal(quote.GetProperty("bankAfter").GetDecimal(), receipt.GetProperty("bankAfter").GetDecimal());
        Assert.Equal(quote.GetProperty("pointsHit").GetInt32(), receipt.GetProperty("pointsHit").GetInt32());
        Assert.Equal(quote.GetProperty("bankAfter").GetDecimal(), (await Ok(await client.GetAsync("/api/v1/fantasy/team"))).GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public async Task T05_a_price_change_between_preview_and_confirm_is_409_price_changed_with_a_new_quote()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var quote = await Preview(client, pool, pool.NextGameweek, ["c12-def1"], ["c15-def5"]);
        await f.Db.ExecAsync("UPDATE app.player_prices SET price = 7.0 WHERE player_id = $1 AND season_id = $2", pool["c15-def5"], f.SeasonId);
        await f.Db.ExecAsync("INSERT INTO app.player_price_changes (player_id, season_id, gameweek_id, old_price, new_price) VALUES ($1, $2, $3, 6.5, 7.0)",
            pool["c15-def5"], f.SeasonId, pool.NextGameweek);
        var r = await Send(client.SendAsync(Confirm(quote, Guid.NewGuid().ToString())));
        AssertError(r, HttpStatusCode.Conflict, "price_changed");
        Assert.Equal(7.0m, r.Body.GetProperty("current").GetProperty("purchaseCost").GetDecimal());
        // Nothing changed: the old quote's squad version is still current.
        var squad = await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"));
        Assert.Equal(quote.GetProperty("squadVersion").GetInt32(), squad.GetProperty("version").GetInt32());
    }

    [SkippableFact]
    public async Task T06_idempotency_same_key_same_body_replays_different_body_is_refused_and_duplicates_charge_once()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var quote = await Preview(client, pool, pool.NextGameweek, ["c9-att1"], ["c15-att3"]);
        var key = Guid.NewGuid().ToString();

        // Two copies of the same request at the same moment (a double tap, a retry on a slow network).
        var both = await Task.WhenAll(client.SendAsync(Confirm(quote, key)), client.SendAsync(Confirm(quote, key)));
        var a = await Ok(both[0]);
        var b = await Ok(both[1]);
        Assert.Equal(a.GetProperty("receiptId").GetInt32(), b.GetProperty("receiptId").GetInt32());
        var replay = await Ok(await client.SendAsync(Confirm(quote, key)));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(a.GetProperty("receiptId").GetInt32(), replay.GetProperty("receiptId").GetInt32());

        var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
        Assert.Equal(33.0m - 5.0m, team.GetProperty("budgetRemaining").GetDecimal());   // charged once
        Assert.Equal(1L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_transfers WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));

        var other = await Preview(client, pool, pool.NextGameweek, ["c10-att1"], ["c15-att2"]);
        AssertError(await Send(client.SendAsync(Confirm(other, key))), HttpStatusCode.Conflict, "idempotency_key_reused");
        var noKey = Confirm(other, "x");
        noKey.Headers.Remove("Idempotency-Key");
        AssertError(await Send(client.SendAsync(noKey)), HttpStatusCode.BadRequest, "idempotency_key_required");
        // A body the API can't read has a code too (not ASP.NET's bare validation shape).
        var unreadable = await Send(client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/confirm", new { gameweekId = pool.NextGameweek }));
        AssertError(unreadable, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(JsonValueKind.Array, unreadable.Body.GetProperty("errors").ValueKind);
    }

    [SkippableFact]
    public async Task T07_a_stale_squad_version_is_409_stale_squad_with_the_current_state()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var squad = await client.GetAsync("/api/v1/fantasy/team/squad");
        var etag = squad.Headers.ETag!.ToString();
        var quote = await Preview(client, pool, pool.NextGameweek, ["c9-att1"], ["c15-att1"]);

        // Another device changes the lineup.
        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/lineup", (Cheap() with { Bench = ["c11-gk1", "c14-att1", "c13-mid1", "c12-def1"] }).Request(pool, pool.NextGameweek)));

        var legacy = await Send(client.SendAsync(Json(HttpMethod.Post, "/api/v1/fantasy/team/transfers",
            Ids(pool, pool.NextGameweek, ["c9-att1"], ["c15-att1"]), ("If-Match", etag))));
        AssertError(legacy, HttpStatusCode.Conflict, "stale_squad");
        Assert.True(legacy.Body.GetProperty("current").TryGetProperty("players", out _));

        var confirm = await Send(client.SendAsync(Confirm(quote, Guid.NewGuid().ToString())));
        AssertError(confirm, HttpStatusCode.Conflict, "stale_squad");
        Assert.True(confirm.Body.GetProperty("current").TryGetProperty("players", out _));
    }

    [Fact]
    public void T08_before_the_gameweek_1_deadline_transfers_are_unlimited_and_free()
    {
        var rules = new TransferRules(new FantasyRules());
        Assert.True(TransferRules.UnlimitedFreeTransfers(targetIsFirstGameweek: true, activeChip: null));
        var gw1 = rules.Calculate(alreadyMade: 6, newTransfers: 5, unlimited: true);
        Assert.Equal(0, gw1.GameweekCost);
        Assert.Null(gw1.FreeTransfersLeft);
        var normal = rules.Calculate(alreadyMade: 0, newTransfers: 3, unlimited: TransferRules.UnlimitedFreeTransfers(false, null));
        Assert.Equal(8, normal.GameweekCost);
    }

    // ---- Area 5: chips ---------------------------------------------------------------------------

    [SkippableFact]
    public async Task C01_one_chip_per_gameweek_cancel_before_the_deadline_and_no_free_hit()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "bench_boost", gameweekId = pool.NextGameweek }));
        AssertError(await Send(client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "triple_captain", gameweekId = pool.NextGameweek })),
            HttpStatusCode.BadRequest, "rule_violation", "chip_one_per_gameweek");

        await Ok(await client.DeleteAsync($"/api/v1/fantasy/team/chips/bench_boost?gameweekId={pool.NextGameweek}"));
        var tc = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "triple_captain", gameweekId = pool.NextGameweek }));
        Assert.Equal("triple_captain", tc.GetProperty("chip").GetString());

        AssertError(await Send(client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "free_hit", gameweekId = pool.NextGameweek })),
            HttpStatusCode.BadRequest, "rule_violation", "chip_unknown");
    }

    [SkippableFact]
    public async Task C02_each_chip_once_per_season_and_wildcard_cannot_be_cancelled()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "bench_boost", gameweekId = pool.NextGameweek }));
        await AtAsync(f, pool.NextDeadline.AddMinutes(1), async () =>
        {
            AssertError(await Send(client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "bench_boost", gameweekId = pool.LaterGameweek })),
                HttpStatusCode.BadRequest, "rule_violation", "chip_used");
            // After the deadline the week's chip can't be cancelled either.
            AssertError(await Send(client.DeleteAsync($"/api/v1/fantasy/team/chips/bench_boost?gameweekId={pool.NextGameweek}")),
                HttpStatusCode.Conflict, "gameweek_locked");

            await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "wildcard", gameweekId = pool.LaterGameweek }));
            AssertError(await Send(client.DeleteAsync($"/api/v1/fantasy/team/chips/wildcard?gameweekId={pool.LaterGameweek}")),
                HttpStatusCode.Conflict, "chip_not_cancellable");
        });
    }

    [Fact]
    public void C03_triple_captain_is_x4_and_passes_to_the_vice_when_the_captain_plays_zero_bench_boost_counts_the_bench()
    {
        var rules = new FantasyRules();
        PointsBreakdown P(int total, int minutes) => new() { Total = total, Minutes = minutes, Fixtures = 1 };
        List<TeamPickPoints> Team(PointsBreakdown captain) =>
        [
            new(1, true, true, false, captain),
            new(2, true, false, true, P(5, 90)),
            .. Enumerable.Range(10, 9).Select(i => new TeamPickPoints(i, true, false, false, P(2, 90))),   // 18
            new(30, false, false, false, P(3, 90)),
        ];

        var tc = new TeamScoring(rules).Score(Team(P(6, 90)), Chips.TripleCaptain, 0);
        Assert.Equal(6 * 4 + 5 * 2 + 18, tc.Total);
        var tcVice = new TeamScoring(rules).Score(Team(P(0, 0)), Chips.TripleCaptain, 0);
        Assert.Equal(4, tcVice.Picks.Single(p => p.PlayerId == 2).Multiplier);
        Assert.Equal(5 * 4 + 18, tcVice.Total);
        var bb = new TeamScoring(rules).Score(Team(P(6, 90)), Chips.BenchBoost, 0);
        Assert.Equal(6 * 3 + 5 * 2 + 18 + 3, bb.Total);
        var none = new TeamScoring(rules).Score(Team(P(6, 90)), null, 0);
        Assert.Equal(6 * 3 + 5 * 2 + 18, none.Total);   // bench not counted without Bench Boost
    }
}
