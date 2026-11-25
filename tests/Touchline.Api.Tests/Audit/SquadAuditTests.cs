using System.Net;
using System.Net.Http.Json;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.Audit.AuditWorld;

namespace Touchline.Api.Tests.Audit;

/// <summary>Audit area 2: building and saving a squad, and every limit on it.</summary>
public sealed class SquadAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private async Task<(HttpStatusCode Status, System.Text.Json.JsonElement Body)> SaveAsync(Pool pool, Spec spec)
    {
        var client = await ManagerWithTeamAsync(f);
        return await Send(client.PutAsJsonAsync("/api/v1/fantasy/team/squad", spec.Request(pool, pool.NextGameweek)));
    }

    [SkippableFact]
    public async Task S01_a_legal_15_saves_and_the_bank_is_the_budget_minus_its_cost()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var r = await SaveAsync(pool, Cheap());
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(15, r.Body.GetProperty("players").GetArrayLength());
        Assert.Equal(100.0m - Cheap().Cost(pool), r.Body.GetProperty("budgetRemaining").GetDecimal());
        Assert.Equal(33.0m, r.Body.GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public async Task S02_exactly_R100m_is_allowed_and_R100_5m_is_rejected_over_budget()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        Assert.Equal(100.0m, Exactly100().Cost(pool));
        var exact = await SaveAsync(pool, Exactly100());
        Assert.Equal(HttpStatusCode.OK, exact.Status);
        Assert.Equal(0.0m, exact.Body.GetProperty("budgetRemaining").GetDecimal());

        var over = Exactly100().Swap("c2-def1", "c2-def2");
        Assert.Equal(100.5m, over.Cost(pool));
        AssertError(await SaveAsync(pool, over), HttpStatusCode.BadRequest, "rule_violation", "over_budget");
    }

    [SkippableFact]
    public async Task S03_position_counts_must_be_exactly_2_5_5_3()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        // Bench defender replaced by a sixth midfielder: 4 DEF, 6 MID.
        var spec = Cheap().Swap("c12-def1", "c12-mid1");
        AssertError(await SaveAsync(pool, spec), HttpStatusCode.BadRequest, "rule_violation", "position_count");
        // Bench goalkeeper replaced by an attacker: 1 GK, 4 ATT.
        AssertError(await SaveAsync(pool, Cheap().Swap("c11-gk1", "c11-att1")), HttpStatusCode.BadRequest, "rule_violation", "position_count");
    }

    [SkippableFact]
    public async Task S04_three_from_one_club_is_fine_and_the_fourth_is_rejected()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var three = Cheap().Swap("c2-def1", "c1-def2").Swap("c3-def1", "c1-def3");   // three defenders from club 1
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(pool, three)).Status);
        var four = three.Swap("c4-def1", "c1-def4");
        AssertError(await SaveAsync(pool, four), HttpStatusCode.BadRequest, "rule_violation", "club_limit");
    }

    [SkippableFact]
    public async Task S05_duplicates_and_ineligible_players_are_rejected_and_an_admin_override_makes_one_pickable()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        AssertError(await SaveAsync(pool, Cheap().Swap("c12-def1", "c1-def1")), HttpStatusCode.BadRequest, "rule_violation", "duplicate_player");

        var unsigned = Cheap().Swap("c12-def1", "x-def");
        AssertError(await SaveAsync(pool, unsigned), HttpStatusCode.BadRequest, "rule_violation", "player_unavailable");

        var admin = NewManager(f, out _);
        await MakeAdminAsync(f, admin);
        await Ok(await admin.PutAsJsonAsync($"/api/v1/admin/players/{pool["x-def"]}/available", new { available = true, reason = "Audit: new signing" }));
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(pool, unsigned)).Status);
    }

    [SkippableTheory]
    [InlineData("2-5-3")]   // only 2 defenders
    [InlineData("4-5-1")]   // not an allowed formation
    [InlineData("4-6-0")]
    public async Task S06_starting_xi_must_be_an_allowed_formation_with_at_least_3_defenders(string formation)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var spec = formation switch
        {
            // 2-5-3: two starting defenders move to the bench; the bench MID and ATT start.
            "2-5-3" => new Spec("2-5-3",
                ["c0-gk1", "c1-def1", "c2-def1", "c5-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c13-mid1", "c9-att1", "c10-att1", "c14-att1"],
                ["c11-gk1", "c3-def1", "c4-def1", "c12-def1"], "c9-att1", "c10-att1"),
            "4-5-1" => new Spec("4-5-1",
                ["c0-gk1", "c1-def1", "c2-def1", "c3-def1", "c4-def1", "c5-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c13-mid1", "c9-att1"],
                ["c11-gk1", "c12-def1", "c10-att1", "c14-att1"], "c9-att1", "c8-mid1"),
            _ => Cheap() with { Formation = formation },
        };
        var r = await SaveAsync(pool, spec);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        var rules = r.Body.GetProperty("violations").EnumerateArray().Select(v => v.GetProperty("rule").GetString()).ToList();
        Assert.True(rules.Intersect(["formation_range", "formation_not_allowed"]).Any(), string.Join(",", rules));
    }

    [SkippableTheory]
    [InlineData("4-3-3")]
    [InlineData("4-4-2")]
    [InlineData("3-5-2")]
    [InlineData("3-4-3")]
    [InlineData("5-3-2")]
    public async Task S07_every_allowed_formation_saves(string formation)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        string[] gk = ["c0-gk1"], benchGk = ["c11-gk1"];
        string[] defs = ["c1-def1", "c2-def1", "c3-def1", "c4-def1", "c12-def1"];
        string[] mids = ["c5-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c13-mid1"];
        string[] atts = ["c9-att1", "c10-att1", "c14-att1"];
        var (d, m, a) = (formation[0] - '0', formation[2] - '0', formation[4] - '0');
        var starters = gk.Concat(defs.Take(d)).Concat(mids.Take(m)).Concat(atts.Take(a)).ToList();
        var bench = benchGk.Concat(defs.Skip(d)).Concat(mids.Skip(m)).Concat(atts.Skip(a)).ToList();
        var spec = new Spec(formation, starters, bench, starters[^1], starters[^2]);
        var r = await SaveAsync(pool, spec);
        Assert.True(r.Status == HttpStatusCode.OK, Describe(r.Body));
    }

    [SkippableFact]
    public async Task S08_xi_needs_exactly_one_goalkeeper_and_the_bench_one_goalkeeper_plus_three_outfield()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        // Both goalkeepers start (one in place of a midfielder), the midfielder sits on the bench.
        var twoGk = new Spec("4-4-2",
            ["c0-gk1", "c11-gk1", "c1-def1", "c2-def1", "c3-def1", "c4-def1", "c5-mid1", "c6-mid1", "c7-mid1", "c9-att1", "c10-att1"],
            ["c8-mid1", "c12-def1", "c13-mid1", "c14-att1"], "c9-att1", "c10-att1");
        var r = await SaveAsync(pool, twoGk);
        AssertError(r, HttpStatusCode.BadRequest, "rule_violation", "starting_gk");
        Assert.Contains(r.Body.GetProperty("violations").EnumerateArray(), v => v.GetProperty("rule").GetString() == "bench_gk");
    }

    [SkippableFact]
    public async Task S09_captain_and_vice_must_be_different_starters()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        AssertError(await SaveAsync(pool, Cheap() with { Vice = "c9-att1" }), HttpStatusCode.BadRequest, "rule_violation", "captain_vice_same");
        AssertError(await SaveAsync(pool, Cheap() with { Captain = "c14-att1" }), HttpStatusCode.BadRequest, "rule_violation", "captain_not_starter");
        AssertError(await SaveAsync(pool, Cheap() with { Vice = "c12-def1" }), HttpStatusCode.BadRequest, "rule_violation", "vice_captain_not_starter");
        AssertError(await SaveAsync(pool, Cheap() with { Captain = null }), HttpStatusCode.BadRequest, "rule_violation", "captain_missing");
    }

    [SkippableFact]
    public async Task S10_a_squad_can_only_be_picked_once_then_lineup_and_transfers_take_over()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithSquadAsync(f, pool);
        var again = await Send(client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Cheap().Request(pool, pool.NextGameweek)));
        AssertError(again, HttpStatusCode.Conflict, "squad_already_picked");
    }

    [SkippableFact]
    public async Task S11_autopick_returns_a_legal_squad_within_budget_and_never_saves()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var pool = await EnsureAsync(f);
        var client = await ManagerWithTeamAsync(f);
        var pick = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/autopick/preview", new { formation = "4-3-3" }));
        Assert.True(pick.GetProperty("valid").GetBoolean(), pick.ToString());
        Assert.Equal(15, pick.GetProperty("players").GetArrayLength());
        Assert.True(pick.GetProperty("cost").GetDecimal() <= 100.0m);

        // Nothing was saved: onboarding still needs a squad.
        Assert.True((await Ok(await client.GetAsync("/api/v1/me"))).GetProperty("onboarding").GetProperty("needsSquad").GetBoolean());

        // And the suggestion is accepted as it stands.
        var players = pick.GetProperty("players").EnumerateArray().ToList();
        var starters = players.Where(p => p.GetProperty("isStarter").GetBoolean()).Select(p => p.GetProperty("playerId").GetInt32()).ToArray();
        var bench = players.Where(p => !p.GetProperty("isStarter").GetBoolean()).OrderBy(p => p.GetProperty("benchOrder").GetInt32()).Select(p => p.GetProperty("playerId").GetInt32()).ToArray();
        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", new
        {
            formation = "4-3-3", starters, bench, captainId = pick.GetProperty("captainId").GetInt32(),
            viceCaptainId = pick.GetProperty("viceCaptainId").GetInt32(), gameweekId = pool.NextGameweek,
        }));

        // A formation that isn't allowed gets an explanation, not a squad.
        var bad = await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team/autopick/preview", new { formation = "4-5-1" }));
        Assert.False(bad.GetProperty("valid").GetBoolean());
        Assert.NotEmpty(bad.GetProperty("explanation").EnumerateArray());
    }
}
