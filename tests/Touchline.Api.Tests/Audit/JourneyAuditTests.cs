using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>
/// Audit area 10: a brand-new manager plays the whole game through the API, the way the app's wizards
/// drive it, with drafts saved along the way. Every step succeeds or returns a code the UI can show;
/// nothing needs a database fix.
/// </summary>
public sealed class JourneyAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task J01_new_manager_to_points_and_explanation_with_no_dead_ends()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var app = NewManager(f, out _);

        // 1. First open: profile created, onboarding says "username", nothing to resume.
        var me = await Ok(await app.GetAsync("/api/v1/me"));
        Assert.Equal("username", me.GetProperty("onboarding").GetProperty("step").GetString());
        Assert.False(me.GetProperty("onboarding").GetProperty("hasDraft").GetBoolean());

        // The onboarding wizard autosaves as the manager types.
        var draft = await Ok(await app.PutAsJsonAsync("/api/v1/me/drafts/onboarding", new { payload = new { username = "Journey" }, version = 0, step = "username" }));
        Assert.Equal(1, draft.GetProperty("version").GetInt32());
        Assert.True((await Ok(await app.GetAsync("/api/v1/me"))).GetProperty("onboarding").GetProperty("hasDraft").GetBoolean());

        // 2. Username (a clash shows a code, then a free name works).
        var taken = "Journey_" + Random.Shared.Next(1000, 9999);
        await Ok(await NewManager(f, out _).PatchAsJsonAsync("/api/v1/me", new { username = taken }));
        AssertError(await Send(app.PatchAsJsonAsync("/api/v1/me", new { username = taken })), HttpStatusCode.Conflict, "username_taken");
        await Ok(await app.PatchAsJsonAsync("/api/v1/me", new { username = taken + "b" }));

        // 3. Favourite club is optional: skipping it doesn't block the next step.
        Assert.Equal("team", (await Ok(await app.GetAsync("/api/v1/me"))).GetProperty("onboarding").GetProperty("step").GetString());

        // 4. Team name.
        AssertError(await Send(app.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "J" })), HttpStatusCode.BadRequest, "rule_violation", "team_name_length");
        await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Journey United" }));

        // 5. Squad wizard: live progress after each pick, then the full lineup.
        var spec = Cheap();
        var picked = new List<int>();
        foreach (var key in spec.Starters.Concat(spec.Bench).Take(7))
        {
            picked.Add(pool[key]);
            var progress = await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", new { players = picked }));
            Assert.False(progress.GetProperty("complete").GetBoolean());
            Assert.Empty(progress.GetProperty("violations").EnumerateArray());
            await Ok(await app.PutAsJsonAsync("/api/v1/me/drafts/squad",
                new { payload = new { players = picked }, version = picked.Count - 1, step = "pick_players" }));
        }
        var full = await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team/squad/validate", spec.Request(pool, null)));
        Assert.True(full.GetProperty("valid").GetBoolean(), full.ToString());
        Assert.Equal(33.0m, full.GetProperty("budget").GetProperty("left").GetDecimal());

        var squad = await Ok(await app.PutAsJsonAsync("/api/v1/fantasy/team/squad", spec.Request(pool, pool.NextGameweek)));
        await Ok(await app.DeleteAsync("/api/v1/me/drafts/squad"));
        await Ok(await app.DeleteAsync("/api/v1/me/drafts/onboarding"));
        me = await Ok(await app.GetAsync("/api/v1/me"));
        Assert.Equal("done", me.GetProperty("onboarding").GetProperty("step").GetString());
        Assert.False(me.GetProperty("onboarding").GetProperty("hasDraft").GetBoolean());

        // 6. Lineup and captain (a substitution), with If-Match from the squad's ETag.
        var etag = (await app.GetAsync("/api/v1/fantasy/team/squad")).Headers.ETag!.ToString();
        var sub = Cheap().Swap("c5-mid1", "TMP").Swap("c13-mid1", "c5-mid1").Swap("TMP", "c13-mid1") with { Captain = "c10-att1", Vice = "c9-att1" };
        var lineupCheck = await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team/lineup/validate", sub.Request(pool, null)));
        Assert.True(lineupCheck.GetProperty("valid").GetBoolean(), lineupCheck.ToString());
        await Ok(await app.SendAsync(Json(HttpMethod.Put, "/api/v1/fantasy/team/lineup", sub.Request(pool, pool.NextGameweek), ("If-Match", etag))));

        // 7. Transfers: check, quote, confirm with an Idempotency-Key.
        var plan = await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team/transfers/validate", new { @out = new[] { pool["c14-att1"] }, @in = new[] { pool["c15-att3"] } }));
        Assert.True(plan.GetProperty("valid").GetBoolean(), plan.ToString());
        var quote = await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team/transfers/preview", new { gameweekId = pool.NextGameweek, @out = new[] { pool["c14-att1"] }, @in = new[] { pool["c15-att3"] } }));
        var receipt = await Ok(await app.SendAsync(Json(HttpMethod.Post, "/api/v1/fantasy/team/transfers/confirm", new
        {
            gameweekId = pool.NextGameweek, @out = new[] { pool["c14-att1"] }, @in = new[] { pool["c15-att3"] },
            squadVersion = quote.GetProperty("squadVersion").GetInt32(), priceRevision = quote.GetProperty("priceRevision").GetInt32(),
            quotedAtUtc = quote.GetProperty("quotedAtUtc").GetDateTimeOffset(),
        }, ("Idempotency-Key", Guid.NewGuid().ToString()))));
        Assert.Equal(28.0m, receipt.GetProperty("bankAfter").GetDecimal());

        // 8. A chip.
        await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/team/chips", new { chip = "triple_captain", gameweekId = pool.NextGameweek }));

        // 9. Create a league and a friend joins it.
        var league = await Ok(await app.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Journey League" }));
        var friend = await ManagerWithSquadAsync(f, pool);
        await Ok(await friend.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code = league.GetProperty("joinCode").GetString() }));
        var standings = await Ok(await app.GetAsync($"/api/v1/fantasy/leagues/{league.GetProperty("id").GetString()}/standings"));
        Assert.Equal(2, standings.GetProperty("rows").GetArrayLength());

        // 10. Points and their explanation, and the snapshot the app caches.
        await Ok(await app.GetAsync($"/api/v1/fantasy/team/points?gameweek={await GameweekNumberAsync(pool.NextGameweek)}"));
        await Ok(await app.GetAsync($"/api/v1/fantasy/team/points/explain?gameweek={await GameweekNumberAsync(pool.NextGameweek)}"));
        var snapshot = await Ok(await app.GetAsync("/api/v1/fantasy/team/snapshot"));
        Assert.Equal("triple_captain", snapshot.GetProperty("squad").GetProperty("chip").GetString());
    }

    private Task<short> GameweekNumberAsync(int id) => Touchline.Api.Tests.Db.TestDatabase.ScalarAsync<short>(f.Db, "SELECT number FROM app.gameweeks WHERE id = $1", id);
}
