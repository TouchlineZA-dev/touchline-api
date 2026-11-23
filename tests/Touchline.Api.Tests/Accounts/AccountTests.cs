using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;

namespace Touchline.Api.Tests.Accounts;

/// <summary>
/// Sprint 3 (03-Leruo-API Stage 3, handbook Gate 5) through the real pipeline: usernames, favourite
/// club, team rename, onboarding, notification preferences, export, deletion as anonymisation and
/// suspension. Each test signs in fresh Supabase users.
/// </summary>
public sealed class AccountTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private HttpClient Client(Guid sub, string? email = "auto", DateTime? expires = null) =>
        f.Api.ClientWithToken(f.Tokens.Create(sub, email == "auto" ? $"fan-{sub:N}@example.com" : email, expires: expires));

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, string url, object body) =>
        client.PatchAsync(url, JsonContent.Create(body));

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 8, 20)];

    [SkippableFact]
    public async Task Onboarding_moves_from_username_to_team_to_squad()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fan = Client(Guid.NewGuid());
        var me = await Ok(await fan.GetAsync("/api/v1/me"));
        var onboarding = me.GetProperty("onboarding");
        Assert.True(onboarding.GetProperty("needsUsername").GetBoolean());
        Assert.True(onboarding.GetProperty("needsTeam").GetBoolean());
        Assert.False(onboarding.GetProperty("needsSquad").GetBoolean());

        var chosen = await Ok(await Patch(fan, "/api/v1/me", new { username = Unique("scout_") }));
        Assert.False(chosen.GetProperty("onboarding").GetProperty("needsUsername").GetBoolean());

        await Ok(await fan.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Onboarding XI" }));
        onboarding = (await Ok(await fan.GetAsync("/api/v1/me"))).GetProperty("onboarding");
        Assert.False(onboarding.GetProperty("needsTeam").GetBoolean());
        Assert.True(onboarding.GetProperty("needsSquad").GetBoolean());
    }

    [SkippableFact]
    public async Task Username_is_unique_ignoring_case_and_unicode_width_and_the_conflict_reveals_no_holder()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var name = Unique("psl_");
        var alice = Client(Guid.NewGuid());
        var aliceMe = await Ok(await Patch(alice, "/api/v1/me", new { username = name }));
        Assert.Equal(name, aliceMe.GetProperty("username").GetString());

        foreach (var attempt in new[] { name.ToUpperInvariant(), ToFullWidth(name) })
        {
            var response = await Patch(Client(Guid.NewGuid()), "/api/v1/me", new { username = attempt });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await Json(response);
            Assert.Equal("username_taken", problem.GetProperty("code").GetString());
            Assert.Equal("Choose another username", problem.GetProperty("title").GetString());
            var raw = problem.GetRawText();
            Assert.DoesNotContain(aliceMe.GetProperty("id").GetInt32().ToString() + ",", raw);
            Assert.DoesNotContain("example.com", raw);
        }
    }

    [SkippableTheory]
    [InlineData("ab")]
    [InlineData("this_name_is_far_too_long")]
    [InlineData("bad name!")]
    [InlineData("Admin")]
    [InlineData("touchline_team")]
    public async Task Invalid_or_reserved_usernames_are_rejected(string username)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var response = await Patch(Client(Guid.NewGuid()), "/api/v1/me", new { username });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Simultaneous_claims_on_one_username_give_one_winner_and_one_stable_conflict()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var name = Unique("race_");
        var a = Client(Guid.NewGuid());
        var b = Client(Guid.NewGuid());
        await Ok(await a.GetAsync("/api/v1/me"));
        await Ok(await b.GetAsync("/api/v1/me"));

        var results = await Task.WhenAll(Patch(a, "/api/v1/me", new { username = name }), Patch(b, "/api/v1/me", new { username = name }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("username_taken", (await Json(loser)).GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Renaming_the_username_keeps_the_account_and_team_ids_and_respects_the_cooldown()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fan = Client(Guid.NewGuid());
        var first = await Ok(await Patch(fan, "/api/v1/me", new { username = Unique("first_") }));
        var team = await Ok(await fan.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Kept Team" }));

        var tooSoon = await Patch(fan, "/api/v1/me", new { username = Unique("second_") });
        Assert.Equal(HttpStatusCode.BadRequest, tooSoon.StatusCode);   // 30-day cooldown after a chosen name

        var id = first.GetProperty("id").GetInt32();
        await f.Db.ExecAsync("UPDATE app.users SET username_changed_at = now() - interval '31 days' WHERE id = $1", id);
        var renamed = await Ok(await Patch(fan, "/api/v1/me", new { username = Unique("second_") }));
        Assert.Equal(id, renamed.GetProperty("id").GetInt32());
        Assert.Equal(team.GetProperty("id").GetInt32(), (await Ok(await fan.GetAsync("/api/v1/fantasy/team"))).GetProperty("id").GetInt32());
        Assert.Equal(2L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.username_history WHERE user_id = $1", id));
    }

    [SkippableFact]
    public async Task Favourite_club_must_play_in_the_current_season_and_can_be_cleared()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await f.FixtureAsync(f.Clock.Now.AddDays(3), 1);   // Home and Away play this season
        var outsider = await f.TeamAsync("Not In League", "NIL");
        var fan = Client(Guid.NewGuid());

        var set = await Ok(await Patch(fan, "/api/v1/me", new { favouriteClubId = f.Home }));
        Assert.Equal(f.Home, set.GetProperty("favouriteClubId").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(fan, "/api/v1/me", new { favouriteClubId = outsider })).StatusCode);
        var cleared = await Ok(await Patch(fan, "/api/v1/me", new { clearFavouriteClub = true }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("favouriteClubId").ValueKind);

        var clubs = await Ok(await f.Api.CreateClient().GetAsync("/api/v1/clubs"));
        var home = clubs.GetProperty("clubs").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == f.Home);
        Assert.True(home.GetProperty("sportmonksTeamId").GetInt64() > 0);
        Assert.Equal("ORL", home.GetProperty("shortCode").GetString());
        Assert.Equal(JsonValueKind.Null, home.GetProperty("crest").ValueKind);   // placeholder crest
    }

    [SkippableFact]
    public async Task Team_names_are_3_to_30_characters_may_repeat_and_renames_keep_the_id_with_history()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fan = Client(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await fan.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "ab" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fan.PostAsJsonAsync("/api/v1/fantasy/team", new { name = new string('x', 31) })).StatusCode);
        var team = await Ok(await fan.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "  Same Name FC  " }));
        Assert.Equal("Same Name FC", team.GetProperty("name").GetString());
        await Ok(await Client(Guid.NewGuid()).PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Same Name FC" }));   // names may repeat

        var renamed = await Ok(await Patch(fan, "/api/v1/fantasy/team", new { name = "Renamed FC" }));
        Assert.Equal(team.GetProperty("id").GetInt32(), renamed.GetProperty("id").GetInt32());
        Assert.Equal("Renamed FC", renamed.GetProperty("name").GetString());
        Assert.Equal("Same Name FC", await f.Db.ScalarAsync<string>(
            "SELECT old_name FROM app.fantasy_team_name_history WHERE fantasy_team_id = $1", team.GetProperty("id").GetInt32()));
    }

    [SkippableFact]
    public async Task Notification_preferences_default_on_and_can_be_changed()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fan = Client(Guid.NewGuid());
        var defaults = await Ok(await fan.GetAsync("/api/v1/me/notifications"));
        Assert.True(defaults.GetProperty("goalAlerts").GetBoolean());
        await Ok(await Patch(fan, "/api/v1/me/notifications", new { goalAlerts = false }));
        var after = await Ok(await fan.GetAsync("/api/v1/me/notifications"));
        Assert.False(after.GetProperty("goalAlerts").GetBoolean());
        Assert.True(after.GetProperty("deadlineReminders").GetBoolean());
    }

    [SkippableFact]
    public async Task Export_and_delete_need_a_recent_sign_in()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var sub = Guid.NewGuid();
        await Ok(await Client(sub).GetAsync("/api/v1/me"));
        // Token issued 55 minutes ago (still valid for 5 more).
        var stale = Client(sub, expires: DateTime.UtcNow.AddMinutes(5));
        foreach (var response in new[] { await stale.GetAsync("/api/v1/me/export"), await stale.DeleteAsync("/api/v1/me") })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("reauthentication_required", (await Json(response)).GetProperty("code").GetString());
        }
        var export = await Ok(await Client(sub).GetAsync("/api/v1/me/export"));
        Assert.Equal($"fan-{sub:N}@example.com", export.GetProperty("profile").GetProperty("email").GetString());
    }

    [SkippableFact]
    public async Task Deleting_anonymises_passes_league_ownership_archives_empty_leagues_and_blocks_the_old_sign_in()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var ownerSub = Guid.NewGuid();
        var owner = Client(ownerSub);
        var friend = Client(Guid.NewGuid());
        var ownerMe = await Ok(await Patch(owner, "/api/v1/me", new { username = Unique("leaving_") }));
        var friendMe = await Ok(await friend.GetAsync("/api/v1/me"));
        var team = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Leaving XI" }));
        await Ok(await friend.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Friend XI" }));
        var shared = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Shared" }));
        var alone = await Ok(await owner.PostAsJsonAsync("/api/v1/fantasy/leagues", new { name = "Alone" }));
        await Ok(await friend.PostAsJsonAsync("/api/v1/fantasy/leagues/join", new { code = shared.GetProperty("joinCode").GetString() }));

        var deleted = await Ok(await owner.DeleteAsync("/api/v1/me"));
        var id = ownerMe.GetProperty("id").GetInt32();
        Assert.Equal($"deleted_{id}", deleted.GetProperty("username").GetString());

        Assert.Equal(friendMe.GetProperty("id").GetInt32(), await f.Db.ScalarAsync<int>("SELECT owner_user_id FROM app.leagues WHERE id = $1", int.Parse(shared.GetProperty("id").GetString()!)));
        Assert.False(await f.Db.ScalarAsync<bool>("SELECT is_active FROM app.leagues WHERE id = $1", int.Parse(alone.GetProperty("id").GetString()!)));
        Assert.Equal($"Deleted manager {team.GetProperty("id").GetInt32()}", await f.Db.ScalarAsync<string>("SELECT name FROM app.fantasy_teams WHERE id = $1", team.GetProperty("id").GetInt32()));
        Assert.True(await f.Db.ScalarAsync<bool>("SELECT email IS NULL AND deleted_at IS NOT NULL AND NOT is_active FROM app.users WHERE id = $1", id));
        Assert.Equal(1L, await f.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_teams WHERE id = $1", team.GetProperty("id").GetInt32()));   // row kept

        var again = await Client(ownerSub).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal("account_deleted", (await Json(again)).GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Suspended_managers_can_read_but_not_write_and_are_hidden_from_overall()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var suspended = Client(Guid.NewGuid());
        var other = Client(Guid.NewGuid());
        var me = await Ok(await suspended.GetAsync("/api/v1/me"));
        var team = await Ok(await suspended.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Suspended XI" }));
        await Ok(await other.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Other XI" }));
        await f.Db.ExecAsync("UPDATE app.users SET suspended_at = now(), suspension_reason = 'test' WHERE id = $1", me.GetProperty("id").GetInt32());

        Assert.True((await Ok(await suspended.GetAsync("/api/v1/me"))).GetProperty("suspended").GetBoolean());
        await Ok(await suspended.GetAsync("/api/v1/fantasy/team"));
        var write = await Patch(suspended, "/api/v1/fantasy/team", new { name = "Sneaky Rename" });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("account_suspended", (await Json(write)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(suspended, "/api/v1/me", new { username = Unique("sneaky_") })).StatusCode);

        var overall = await Ok(await other.GetAsync("/api/v1/fantasy/leagues/overall/standings"));
        Assert.DoesNotContain(overall.GetProperty("rows").EnumerateArray(), r => r.GetProperty("fantasyTeamId").GetInt32() == team.GetProperty("id").GetInt32());
        // Leaderboards publish only the username and team name.
        Assert.All(overall.GetProperty("rows").EnumerateArray(), r => Assert.DoesNotContain("example.com", r.GetRawText()));
    }

    [SkippableFact]
    public async Task Expired_tokens_are_refused()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        // Beyond the 5-minute clock skew JWT validation allows.
        var response = await Client(Guid.NewGuid(), expires: DateTime.UtcNow.AddMinutes(-10)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string ToFullWidth(string s) =>
        new(s.Select(c => c is >= '!' and <= '~' ? (char)(c - '!' + '！') : c).ToArray());
}
