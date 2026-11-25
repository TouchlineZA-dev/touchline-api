using System.Net;
using System.Net.Http.Json;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>Audit area 1: account and onboarding.</summary>
public sealed class AccountAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task A01_first_me_creates_the_profile_and_onboarding_flags_follow_each_step()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = NewManager(f, out _);

        var me = await Ok(await client.GetAsync("/api/v1/me"));
        var o = me.GetProperty("onboarding");
        Assert.True(o.GetProperty("needsUsername").GetBoolean());
        Assert.True(o.GetProperty("needsTeam").GetBoolean());
        Assert.False(o.GetProperty("needsSquad").GetBoolean());
        Assert.Equal("username", o.GetProperty("step").GetString());

        await Ok(await client.PatchAsJsonAsync("/api/v1/me", new { username = "Onboard_" + Random.Shared.Next(1000, 9999) }));
        o = (await Ok(await client.GetAsync("/api/v1/me"))).GetProperty("onboarding");
        Assert.False(o.GetProperty("needsUsername").GetBoolean());
        Assert.Equal("team", o.GetProperty("step").GetString());

        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Onboarding FC" }));
        o = (await Ok(await client.GetAsync("/api/v1/me"))).GetProperty("onboarding");
        Assert.False(o.GetProperty("needsTeam").GetBoolean());
        Assert.True(o.GetProperty("needsSquad").GetBoolean());
        Assert.Equal("squad", o.GetProperty("step").GetString());

        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Cheap().Request(pool, pool.NextGameweek)));
        o = (await Ok(await client.GetAsync("/api/v1/me"))).GetProperty("onboarding");
        Assert.False(o.GetProperty("needsSquad").GetBoolean());
        Assert.Equal("done", o.GetProperty("step").GetString());

        // Repeating /me never creates a second profile.
        var again = await Ok(await client.GetAsync("/api/v1/me"));
        Assert.Equal(me.GetProperty("id").GetInt32(), again.GetProperty("id").GetInt32());
    }

    [SkippableTheory]
    [InlineData("ab", "username_length")]
    [InlineData("abcdefghijklmnopqrstu", "username_length")]   // 21 characters
    [InlineData("has space", "username_characters")]
    [InlineData("dash-name", "username_characters")]
    [InlineData("admin", "username_reserved")]
    [InlineData("Touchline_Fan", "username_reserved")]
    public async Task A02_invalid_usernames_are_rejected_with_a_named_rule(string username, string rule)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var client = NewManager(f, out _);
        var r = await Send(client.PatchAsJsonAsync("/api/v1/me", new { username }));
        AssertError(r, HttpStatusCode.BadRequest, "rule_violation", rule);
    }

    [SkippableFact]
    public async Task A03_usernames_of_3_and_20_characters_with_letters_digits_and_underscores_are_accepted()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var tag = Random.Shared.Next(10, 99).ToString();
        await Ok(await NewManager(f, out _).PatchAsJsonAsync("/api/v1/me", new { username = "a" + tag }));
        await Ok(await NewManager(f, out _).PatchAsJsonAsync("/api/v1/me", new { username = ("Max_20_chars_" + tag + "xxxxxxx")[..20] }));
    }

    [SkippableFact]
    public async Task A04_uniqueness_ignores_case_and_nfkc_width_and_a_clash_is_409_username_taken()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var name = "Clash_" + Random.Shared.Next(1000, 9999);
        await Ok(await NewManager(f, out _).PatchAsJsonAsync("/api/v1/me", new { username = name }));

        var upper = await Send(NewManager(f, out _).PatchAsJsonAsync("/api/v1/me", new { username = name.ToUpperInvariant() }));
        AssertError(upper, HttpStatusCode.Conflict, "username_taken");

        // Full-width letters normalise (NFKC) to the same ASCII name.
        var fullWidth = string.Concat(name.Select(ch => ch is >= '!' and <= '~' ? (char)(ch + 0xFEE0) : ch));
        var width = await Send(NewManager(f, out _).PatchAsJsonAsync("/api/v1/me", new { username = fullWidth }));
        AssertError(width, HttpStatusCode.Conflict, "username_taken");
    }

    [SkippableFact]
    public async Task A05_after_choosing_a_username_the_cooldown_blocks_another_change()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var client = NewManager(f, out _);
        var first = "Cool_" + Random.Shared.Next(1000, 9999);
        await Ok(await client.PatchAsJsonAsync("/api/v1/me", new { username = first }));
        // Same name with different case is not a change of name.
        await Ok(await client.PatchAsJsonAsync("/api/v1/me", new { username = first.ToLowerInvariant() }));
        var again = await Send(client.PatchAsJsonAsync("/api/v1/me", new { username = first + "x" }));
        AssertError(again, HttpStatusCode.BadRequest, "username_cooldown");
    }

    [SkippableFact]
    public async Task A06_favourite_club_can_be_set_cleared_and_must_be_a_current_club()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = NewManager(f, out _);
        var set = await Ok(await client.PatchAsJsonAsync("/api/v1/me", new { favouriteClubId = pool.Clubs[3] }));
        Assert.Equal(pool.Clubs[3], set.GetProperty("favouriteClubId").GetInt32());
        var cleared = await Ok(await client.PatchAsJsonAsync("/api/v1/me", new { clearFavouriteClub = true }));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cleared.GetProperty("favouriteClubId").ValueKind);
        var unknown = await Send(client.PatchAsJsonAsync("/api/v1/me", new { favouriteClubId = 999_999 }));
        AssertError(unknown, HttpStatusCode.BadRequest, "rule_violation", "favourite_club_unknown");
    }

    [SkippableTheory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("123456789012345678901234567890", true)]    // 30
    [InlineData("1234567890123456789012345678901", false)]  // 31
    [InlineData("   ab   ", false)]                          // trimmed to 2
    public async Task A07_team_names_are_3_to_30_characters_on_create(string name, bool ok)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var r = await Send(NewManager(f, out _).PostAsJsonAsync("/api/v1/fantasy/team", new { name }));
        if (ok) Assert.Equal(HttpStatusCode.OK, r.Status == HttpStatusCode.Created ? HttpStatusCode.OK : r.Status);
        else AssertError(r, HttpStatusCode.BadRequest, "rule_violation", "team_name_length");
    }

    [SkippableFact]
    public async Task A08_rename_keeps_the_team_id_records_history_and_checks_the_length()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await EnsureAsync(f);
        var client = await ManagerWithTeamAsync(f, "Before Rename");
        var before = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
        var after = await Ok(await client.PatchAsJsonAsync("/api/v1/fantasy/team", new { name = "After Rename" }));
        Assert.Equal(before.GetProperty("id").GetInt32(), after.GetProperty("id").GetInt32());
        Assert.Equal("After Rename", after.GetProperty("name").GetString());
        Assert.Equal(1L, await Touchline.Api.Tests.Db.TestDatabase.ScalarAsync<long>(f.Db,
            "SELECT count(*) FROM app.fantasy_team_name_history WHERE fantasy_team_id = $1 AND old_name = 'Before Rename' AND new_name = 'After Rename'",
            before.GetProperty("id").GetInt32()));
        AssertError(await Send(client.PatchAsJsonAsync("/api/v1/fantasy/team", new { name = "x" })), HttpStatusCode.BadRequest, "rule_violation", "team_name_length");
        // A second team in the same season is refused with its own code.
        AssertError(await Send(client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Second Team" })), HttpStatusCode.Conflict, "team_exists");
    }
}
