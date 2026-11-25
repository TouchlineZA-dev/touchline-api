using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.SafeWrites.FantasyWorld;

namespace Touchline.Api.Tests.SafeWrites;

/// <summary>
/// Sprint 4 (03-Leruo-API Stage 4, handbook Gate 6) against canonical Postgres through the real
/// pipeline: target gameweek, atomic versions, quotes, idempotent confirmation, receipts, chips,
/// the ledger and the deadline lock.
/// </summary>
public sealed class SafeWriteTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private async Task<(Pool Pool, HttpClient Client)> Manager()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        return (pool, await ManagerWithSquadAsync(f, pool));
    }

    private static HttpRequestMessage Confirm(object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/fantasy/team/transfers/confirm") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static object ConfirmBody(JsonElement quote, int[] outIds, int[] inIds) => new
    {
        gameweekId = quote.GetProperty("gameweekId").GetInt32(),
        @out = outIds,
        @in = inIds,
        squadVersion = quote.GetProperty("squadVersion").GetInt32(),
        priceRevision = quote.GetProperty("priceRevision").GetInt32(),
        quotedAtUtc = quote.GetProperty("quotedAtUtc").GetDateTimeOffset(),
        saleProceeds = quote.GetProperty("saleProceeds").GetDecimal(),
        purchaseCost = quote.GetProperty("purchaseCost").GetDecimal(),
    };

    private async Task<(JsonElement Quote, int[] Out, int[] In)> Quote(HttpClient client, Pool pool, string outKey = "c4-mid1", string inKey = "c5-mid2")
    {
        int[] outIds = [pool.Players[outKey]], inIds = [pool.Players[inKey]];
        var quote = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview", new { gameweekId = pool.NextGameweek, @out = outIds, @in = inIds }));
        return (quote, outIds, inIds);
    }

    /// <summary>Transfer rows for this manager's team (the database is shared by the tests in this class).</summary>
    private async Task<long> TransferRows(HttpClient client)
    {
        var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
        return await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_transfers WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32());
    }

    private static async Task<string> Describe(IEnumerable<HttpResponseMessage> responses) =>
        string.Join(" | ", await Task.WhenAll(responses.Select(async r => $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}")));

    [SkippableFact]
    public async Task Writes_must_name_the_target_gameweek_and_a_locked_week_is_refused_with_the_current_state()
    {
        var (pool, client) = await Manager();
        var squad = await Ok(await client.GetAsync($"/api/v1/fantasy/team/squad"));
        Assert.Equal(pool.NextGameweek, squad.GetProperty("gameweek").GetProperty("id").GetInt32());
        Assert.True(squad.TryGetProperty("serverTimeUtc", out _));
        Assert.True(squad.GetProperty("gameweek").TryGetProperty("deadline", out _));

        var noTarget = await Send(client.PutAsJsonAsync("/api/v1/fantasy/team/lineup", new { formation = "4-4-2", starters = Array.Empty<int>(), bench = Array.Empty<int>() }));
        Assert.Equal(HttpStatusCode.BadRequest, noTarget.Status);
        Assert.Equal("target_gameweek_required", noTarget.Body.GetProperty("code").GetString());

        var locked = await Send(client.PutAsJsonAsync("/api/v1/fantasy/team/lineup", Squad(pool, pool.PastGameweek)));
        Assert.Equal(HttpStatusCode.Conflict, locked.Status);
        Assert.Equal("gameweek_locked", locked.Body.GetProperty("code").GetString());
        Assert.Equal(pool.NextGameweek, locked.Body.GetProperty("current").GetProperty("nextGameweek").GetProperty("id").GetInt32());
    }

    [SkippableFact]
    public async Task Two_devices_editing_one_version_one_wins_and_the_other_gets_a_recoverable_409()
    {
        var (pool, client) = await Manager();
        var etag = (await client.GetAsync("/api/v1/fantasy/team/squad")).Headers.ETag!.Tag;

        HttpRequestMessage Lineup(int captain)
        {
            var body = JsonSerializer.SerializeToNode(Squad(pool, pool.NextGameweek))!.AsObject();
            body["captainId"] = captain;
            body["viceCaptainId"] = pool.Players["c0-att"] == captain ? pool.Players["c1-att"] : pool.Players["c0-att"];
            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/fantasy/team/lineup") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            return request;
        }

        var results = await Task.WhenAll(client.SendAsync(Lineup(pool.Players["c3-mid1"])), client.SendAsync(Lineup(pool.Players["c4-mid1"])));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK) == 1 && results.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, await Describe(results));
        var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("stale_squad", JsonDocument.Parse(await loser.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(2, (await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"))).GetProperty("version").GetInt32());
    }

    [SkippableFact]
    public async Task Concurrent_first_saves_keep_one_squad_and_the_other_gets_a_409()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        var team = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Race XI" }));

        var results = await Task.WhenAll(
            client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)),
            client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK) == 1 && results.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, await Describe(results));
        var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("squad_already_picked", JsonDocument.Parse(await loser.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(15L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_squads WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));
    }

    [SkippableFact]
    public async Task Preview_quotes_costs_bank_free_transfers_hit_and_versions_without_saving()
    {
        var (pool, client) = await Manager();
        var (quote, _, inIds) = await Quote(client, pool);
        Assert.True(quote.GetProperty("valid").GetBoolean());
        Assert.Equal(6.0m, quote.GetProperty("saleProceeds").GetDecimal());
        Assert.Equal(6.0m, quote.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(quote.GetProperty("bankBefore").GetDecimal(), quote.GetProperty("bankAfter").GetDecimal());
        Assert.Equal(0, quote.GetProperty("pointsHit").GetInt32());   // the team's first gameweek: unlimited free transfers
        Assert.Equal(1, quote.GetProperty("squadVersion").GetInt32());
        Assert.Equal(0L, await TransferRows(client));

        var invalid = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview",
            new { gameweekId = pool.NextGameweek, @out = new[] { pool.Players["c4-mid1"] }, @in = new[] { pool.Players["c5-att"] } }));
        Assert.False(invalid.GetProperty("valid").GetBoolean());
        Assert.NotEmpty(invalid.GetProperty("violations").EnumerateArray());
    }

    [SkippableFact]
    public async Task A_response_lost_after_commit_is_recovered_by_retrying_with_the_same_key()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool);
        var key = Guid.NewGuid().ToString();
        var body = ConfirmBody(quote, outIds, inIds);

        var first = await Ok(await client.SendAsync(Confirm(body, key)));
        Assert.False(first.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, first.GetProperty("squadVersionBefore").GetInt32());
        Assert.Equal(2, first.GetProperty("squadVersionAfter").GetInt32());

        var retry = await Ok(await client.SendAsync(Confirm(body, key)));   // the app never saw the first response
        Assert.True(retry.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.GetProperty("receiptId").GetInt32(), retry.GetProperty("receiptId").GetInt32());
        Assert.Equal(1L, await TransferRows(client));
        Assert.Equal(2, (await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"))).GetProperty("version").GetInt32());
    }

    [SkippableFact]
    public async Task The_same_key_with_a_different_request_is_refused_and_a_missing_key_is_400()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool);
        var key = Guid.NewGuid().ToString();
        await Ok(await client.SendAsync(Confirm(ConfirmBody(quote, outIds, inIds), key)));

        var (quote2, out2, in2) = await Quote(client, pool, "c3-mid1", "c4-mid2");
        var reused = await Send(client.SendAsync(Confirm(ConfirmBody(quote2, out2, in2), key)));
        Assert.Equal(HttpStatusCode.Conflict, reused.Status);
        Assert.Equal("idempotency_key_reused", reused.Body.GetProperty("code").GetString());
        Assert.Equal(1L, await TransferRows(client));   // only the first confirmation

        var missing = await Send(client.SendAsync(Confirm(ConfirmBody(quote2, out2, in2), null)));
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Equal("idempotency_key_required", missing.Body.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task A_price_change_between_preview_and_confirm_returns_409_with_a_new_quote()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool, "c2-def1", "c5-def2");
        await f.Db.ExecAsync("UPDATE app.player_prices SET price = 5.5, updated_at = now() WHERE player_id = $1", inIds[0]);
        await f.Db.ExecAsync("INSERT INTO app.player_price_changes (player_id, season_id, gameweek_id, old_price, new_price) VALUES ($1, $2, $3, 5.0, 5.5)",
            inIds[0], f.SeasonId, pool.NextGameweek);
        try
        {
            var result = await Send(client.SendAsync(Confirm(ConfirmBody(quote, outIds, inIds), Guid.NewGuid().ToString())));
            Assert.Equal(HttpStatusCode.Conflict, result.Status);
            Assert.Equal("price_changed", result.Body.GetProperty("code").GetString());
            Assert.Equal(5.5m, result.Body.GetProperty("current").GetProperty("purchaseCost").GetDecimal());
            Assert.Equal(0L, await TransferRows(client));
        }
        finally
        {
            await f.Db.ExecAsync("UPDATE app.player_prices SET price = 5.0 WHERE player_id = $1", inIds[0]);
        }
    }

    [SkippableFact]
    public async Task Duplicate_transfers_from_two_clients_charge_once()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool, "c2-def2", "c4-def2");
        var body = ConfirmBody(quote, outIds, inIds);
        var results = await Task.WhenAll(
            client.SendAsync(Confirm(body, Guid.NewGuid().ToString())),
            client.SendAsync(Confirm(body, Guid.NewGuid().ToString())));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
        Assert.Equal(1L, await TransferRows(client));
        var squad = await Ok(await client.GetAsync("/api/v1/fantasy/team/squad"));
        Assert.Equal(1, squad.GetProperty("transfersMade").GetInt32());
    }

    [SkippableFact]
    public async Task A_quote_that_straddles_the_deadline_is_refused_and_nothing_is_saved()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool, "c5-def1", "c5-def2");
        var start = f.Clock.Now;
        f.Clock.Now = start.AddDays(3).AddMinutes(1);   // past the next gameweek's deadline
        try
        {
            var result = await Send(client.SendAsync(Confirm(ConfirmBody(quote, outIds, inIds), Guid.NewGuid().ToString())));
            Assert.Equal(HttpStatusCode.Conflict, result.Status);
            Assert.Equal("gameweek_locked", result.Body.GetProperty("code").GetString());
            Assert.Equal(0L, await TransferRows(client));
        }
        finally
        {
            f.Clock.Now = start;
        }
    }

    [SkippableFact]
    public async Task A_deadline_during_an_api_restart_keeps_the_persisted_receipt_recoverable()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool, "c3-def1", "c4-def2");
        var key = Guid.NewGuid().ToString();
        var body = ConfirmBody(quote, outIds, inIds);
        var receipt = await Ok(await client.SendAsync(Confirm(body, key)));

        // A new API process starts after the deadline; the app retries the request it never saw answered.
        using var restarted = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, new TestClock(f.Clock.Now.AddDays(3).AddHours(1)));
        var retryClient = restarted.CreateClient();
        retryClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        var recovered = await Ok(await retryClient.SendAsync(Confirm(body, key)));
        Assert.Equal(receipt.GetProperty("receiptId").GetInt32(), recovered.GetProperty("receiptId").GetInt32());
        Assert.True(recovered.GetProperty("replayed").GetBoolean());
        Assert.Equal(1L, await TransferRows(client));
    }

    [SkippableFact]
    public async Task An_expired_quote_and_insufficient_funds_have_their_own_codes()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool, "c4-def1", "c4-def2");
        var stale = JsonSerializer.SerializeToNode(ConfirmBody(quote, outIds, inIds))!.AsObject();
        stale["quotedAtUtc"] = f.Clock.Now.AddMinutes(-20);
        var expired = await Send(client.SendAsync(Confirm(stale, Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.Conflict, expired.Status);
        Assert.Equal("quote_expired", expired.Body.GetProperty("code").GetString());

        var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
        await f.Db.ExecAsync("UPDATE app.fantasy_teams SET budget_remaining = 0 WHERE id = $1", team.GetProperty("id").GetInt32());
        var (q4, o4, i4) = await Quote(client, pool, "c1-gk", "c2-gk");   // 4.5 for 4.5: affordable even with bank 0
        Assert.True(q4.GetProperty("valid").GetBoolean());
        await f.Db.ExecAsync("UPDATE app.player_prices SET price = 6.0 WHERE player_id = $1", i4[0]);
        try
        {
            var (q5, o5, i5) = await Quote(client, pool, "c1-gk", "c2-gk");
            Assert.False(q5.GetProperty("valid").GetBoolean());
            var poor = await Send(client.SendAsync(Confirm(ConfirmBody(q5, o5, i5), Guid.NewGuid().ToString())));
            Assert.Equal(HttpStatusCode.BadRequest, poor.Status);
            Assert.Equal("insufficient_funds", poor.Body.GetProperty("code").GetString());
        }
        finally
        {
            await f.Db.ExecAsync("UPDATE app.player_prices SET price = 4.5 WHERE player_id = $1", i4[0]);
        }
    }

    [SkippableFact]
    public async Task Autopick_preview_returns_a_legal_squad_or_an_explanation_and_saves_nothing()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var client = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        var team = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Autopick XI" }));
        var pick = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/autopick/preview", new { formation = "4-4-2" }));
        Assert.True(pick.GetProperty("valid").GetBoolean(), pick.GetRawText());
        Assert.Equal(15, pick.GetProperty("players").GetArrayLength());
        Assert.True(pick.GetProperty("cost").GetDecimal() <= 100.0m);
        Assert.Equal(0L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_squads WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));

        var bad = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/autopick/preview", new { formation = "2-6-2" }));
        Assert.False(bad.GetProperty("valid").GetBoolean());
        Assert.NotEmpty(bad.GetProperty("explanation").EnumerateArray());
    }

    [SkippableFact]
    public async Task Chips_show_entitlements_triple_captain_can_be_cancelled_and_wildcard_cannot()
    {
        var (pool, client) = await Manager();
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "triple_captain", gameweekId = pool.NextGameweek }));
        var chips = await Ok(await client.GetAsync("/api/v1/fantasy/team/chips"));
        var tc = chips.GetProperty("chips").EnumerateArray().Single(c => c.GetProperty("chip").GetString() == "triple_captain");
        Assert.Equal("active", tc.GetProperty("status").GetString());
        Assert.True(tc.GetProperty("canCancel").GetBoolean());

        var cancelled = await Ok(await client.DeleteAsync($"/api/v1/fantasy/team/chips/triple_captain?gameweekId={pool.NextGameweek}"));
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("chip").ValueKind);
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "wildcard", gameweekId = pool.NextGameweek }));   // reuses the cancelled row
        var wildcard = await Send(client.DeleteAsync($"/api/v1/fantasy/team/chips/wildcard?gameweekId={pool.NextGameweek}"));
        Assert.Equal(HttpStatusCode.Conflict, wildcard.Status);
        Assert.Equal("chip_not_cancellable", wildcard.Body.GetProperty("code").GetString());
        var after = await Ok(await client.GetAsync("/api/v1/fantasy/team/chips"));
        Assert.Equal("available", after.GetProperty("chips").EnumerateArray().Single(c => c.GetProperty("chip").GetString() == "triple_captain").GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task The_ledger_shows_granted_and_used_and_the_deadline_lock_freezes_and_reconciles_entries()
    {
        var (pool, client) = await Manager();
        var (quote, outIds, inIds) = await Quote(client, pool, "c5-mid1", "c2-mid1");
        await Ok(await client.SendAsync(Confirm(ConfirmBody(quote, outIds, inIds), Guid.NewGuid().ToString())));

        var ledger = await Ok(await client.GetAsync("/api/v1/fantasy/team/transfers/ledger"));
        var week = ledger.GetProperty("gameweeks").EnumerateArray().Single(g => g.GetProperty("gameweekId").GetInt32() == pool.NextGameweek);
        Assert.Equal(1, week.GetProperty("used").GetInt32());
        Assert.Equal(JsonValueKind.Null, week.GetProperty("granted").ValueKind);   // team's first week: unlimited
        Assert.False(ledger.GetProperty("rollover").GetBoolean());

        var adminSub = Guid.NewGuid();
        var admin = f.Api.ClientWithToken(f.Tokens.Create(adminSub, null));
        var adminMe = await Ok(await admin.GetAsync("/api/v1/me"));
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", adminMe.GetProperty("id").GetInt32());

        var tooEarly = await Send(admin.PostAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/lock", null));
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.Status);

        var start = f.Clock.Now;
        f.Clock.Now = start.AddDays(3).AddMinutes(5);
        try
        {
            var report = await Ok(await admin.PostAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/lock", null));
            Assert.True(report.GetProperty("reconciled").GetBoolean(), report.GetRawText());
            Assert.True(report.GetProperty("entriesLocked").GetInt32() >= 1);
            var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
            var snapshot = await f.Db.ScalarAsync<string>("SELECT locked_snapshot::text FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2",
                team.GetProperty("id").GetInt32(), pool.NextGameweek);
            Assert.Equal(1, JsonDocument.Parse(snapshot).RootElement.GetProperty("transfersMade").GetInt32());

            await Ok(await admin.PostAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/lock", null));   // running again changes nothing
            Assert.Equal(snapshot, await f.Db.ScalarAsync<string>("SELECT locked_snapshot::text FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2",
                team.GetProperty("id").GetInt32(), pool.NextGameweek));

            var nonAdmin = await Send(client.PostAsync($"/api/v1/admin/gameweeks/{pool.NextGameweek}/lock", null));
            Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.Status);
        }
        finally
        {
            f.Clock.Now = start;
        }
    }
}
