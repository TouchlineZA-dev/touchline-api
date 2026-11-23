using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Reference;

/// <summary>
/// The worker owner's GW9 replay pack (Pirates 2–1 Chiefs, fixture 1393) step by step through the
/// API, as runbook Part 1b asks: lineups only → goals and a sub → full time → scorer corrections.
/// Each test builds its own database so the steps run in order.
/// </summary>
public sealed class ReplayTests
{
    private const long Mbuthuma = 37639894, Mokobodi = 37723175, Moremi = 37551389, Ndaba = 37708606, Lungu = 21395025, Mmodi = 22860191;
    private static readonly DateTimeOffset AfterFullTime = new(2026, 10, 31, 16, 0, 0, TimeSpan.Zero);

    private sealed class Replay : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required TestApiFactory Api { get; init; }
        public required TestClock Clock { get; init; }
        public required int SeasonId { get; init; }

        public async Task Step(string step, DateTimeOffset observedAt)
        {
            Clock.Now = observedAt;
            await ReferenceData.ApplyReplayStepAsync(Db, step, SeasonId);
        }

        public async Task<JsonElement> Detail()
        {
            var response = await Api.CreateClient().GetAsync("/api/v1/fixtures/1393");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        /// <summary>Player points for fixture 1393 by Sportmonks id, from the production scoring path.</summary>
        public async Task<Dictionary<long, PointsBreakdown>> Points()
        {
            using var scope = Api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
            var fixture = await db.Fixtures.SingleAsync(x => x.Id == 1393);
            var facts = await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync([fixture], Clock.Now);
            var ids = await db.Players.ToDictionaryAsync(p => p.Id, p => p.SportmonksPlayerId);
            var scoring = new PlayerScoring(new FantasyRules());
            return facts.ToDictionary(x => ids[x.PlayerId], x => scoring.ScoreFixture(x.Facts, x.Position));
        }

        public ValueTask DisposeAsync() { Api.Dispose(); return ValueTask.CompletedTask; }
    }

    private static async Task<Replay> StartAsync()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        Skip.If(server is null, skip);
        var db = await TestDatabase.CreateAsync(server!, "replay");
        var season = await ReferenceData.SeasonAsync(db);
        await ReferenceData.TeamsAndPlayersAsync(db, ReferenceData.Gw9());
        var clock = new TestClock(new DateTimeOffset(2026, 10, 31, 13, 0, 0, TimeSpan.Zero));
        return new Replay { Db = db, Api = new TestApiFactory(db.ApiConnectionString, new TestTokens(), clock), Clock = clock, SeasonId = season };
    }

    private static async Task PlayToFullTime(Replay r)
    {
        await r.Step("step0_1300_prematch", new DateTimeOffset(2026, 10, 31, 13, 0, 0, TimeSpan.Zero));
        await r.Step("step1_1400_first_half_goal", new DateTimeOffset(2026, 10, 31, 14, 0, 0, TimeSpan.Zero));
        await r.Step("step2_1500_sub_and_goals", new DateTimeOffset(2026, 10, 31, 15, 0, 0, TimeSpan.Zero));
        await r.Step("step3_1600_full_time", AfterFullTime);
    }

    private static int Goals(JsonElement detail) =>
        detail.GetProperty("events").GetProperty("items").EnumerateArray().Count(e => e.GetProperty("type").GetString() == "goal");

    [SkippableFact]
    public async Task Steps_0_to_3_lineups_then_goals_and_a_sub_then_full_time()
    {
        await using var r = await StartAsync();

        await r.Step("step0_1300_prematch", new DateTimeOffset(2026, 10, 31, 13, 0, 0, TimeSpan.Zero));
        var d0 = await r.Detail();
        Assert.Equal("Scheduled", d0.GetProperty("fixture").GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, d0.GetProperty("fixture").GetProperty("home").GetProperty("score").ValueKind);
        Assert.Equal("available", d0.GetProperty("coverage").GetProperty("lineups").GetString());
        Assert.Equal("pending", d0.GetProperty("coverage").GetProperty("events").GetString());
        Assert.Empty(await r.Points());   // not started: nobody scores

        await r.Step("step1_1400_first_half_goal", new DateTimeOffset(2026, 10, 31, 14, 0, 0, TimeSpan.Zero));
        var d1 = await r.Detail();
        Assert.Equal("FirstHalf", d1.GetProperty("fixture").GetProperty("phase").GetString());
        Assert.Equal(1, d1.GetProperty("fixture").GetProperty("home").GetProperty("score").GetInt32());
        var goal = d1.GetProperty("events").GetProperty("items")[0];
        Assert.Equal("assist", goal.GetProperty("relatedRole").GetString());
        Assert.Equal(3, (await r.Points())[Mbuthuma].GoalPoints);

        await r.Step("step2_1500_sub_and_goals", new DateTimeOffset(2026, 10, 31, 15, 0, 0, TimeSpan.Zero));
        var d2 = await r.Detail();
        Assert.Equal("SecondHalf", d2.GetProperty("fixture").GetProperty("phase").GetString());
        var sub = d2.GetProperty("substitutions").EnumerateArray().Single();
        Assert.Equal("46", sub.GetProperty("display").GetString());
        Assert.Equal("Ghampani Lungu", sub.GetProperty("playerOn").GetProperty("name").GetString());
        Assert.Equal("Nkosikhona Ndaba", sub.GetProperty("playerOff").GetProperty("name").GetString());

        await r.Step("step3_1600_full_time", AfterFullTime);
        var d3 = await r.Detail();
        Assert.Equal("Finished", d3.GetProperty("fixture").GetProperty("status").GetString());
        Assert.Equal(3, Goals(d3));
        var points = await r.Points();
        Assert.Equal(5, points[Mbuthuma].Total);   // full match 2 + goal 3 (the pack has no 75' sub)
        Assert.Equal(6, points[Mokobodi].Total);   // full match 2 + 2 assists
        Assert.Equal(5, points[Moremi].Total);
        Assert.Equal(46, points[Ndaba].Minutes);   // off at 46' plays 46
        Assert.Equal(1, points[Ndaba].Total);
        Assert.Equal(45, points[Lungu].Minutes);   // on at 46' plays 45
        Assert.Equal(1, points[Lungu].Total);
        Assert.Equal(5, points[Mmodi].Total);

        // Step 3 note: the week stays pending for the worker's 4 h settle window. No completeness row exists,
        // so the gate is not marked; Sprint 5's settlement keeps it awaiting review.
        Assert.Equal(0L, await r.Db.ScalarAsync<long>("SELECT count(*) FROM sync.gameweek_sync_status"));
    }

    [SkippableFact]
    public async Task Step_4a_reattribution_under_the_same_event_id_moves_the_points_once()
    {
        await using var r = await StartAsync();
        await PlayToFullTime(r);
        var before = (await r.Detail()).GetProperty("revision").GetString();

        await r.Step("step4a_scorer_correction_in_place", AfterFullTime.AddHours(1));
        var detail = await r.Detail();
        Assert.NotEqual(before, detail.GetProperty("revision").GetString());
        Assert.Equal(3, Goals(detail));
        var points = await r.Points();
        Assert.Equal(0, points[Mbuthuma].Goals);
        Assert.Equal(2, points[Mbuthuma].Total);
        Assert.Equal(1, points[Mokobodi].Goals);
        Assert.Equal(1, points[Mokobodi].Assists);   // the 24' assist went with the re-attribution; the 55' one stays
        Assert.Equal(7, points[Mokobodi].Total);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Step_4b_reissue_under_a_new_id_shows_three_goals_and_no_double_points(bool softRescind)
    {
        await using var r = await StartAsync();
        await PlayToFullTime(r);

        await r.Step("step4b_scorer_correction_reissued", AfterFullTime.AddHours(1));
        // After the worker fix (default decision 2): the stale event is rescinded (preferred) or removed.
        if (softRescind) await r.Db.ExecAsync("UPDATE football.match_events SET rescinded = true WHERE sportmonks_event_id = 158473513");
        else await r.Db.ExecAsync("DELETE FROM football.match_events WHERE sportmonks_event_id = 158473513");

        var detail = await r.Detail();
        Assert.Equal(3, Goals(detail));
        Assert.Equal(2, detail.GetProperty("fixture").GetProperty("home").GetProperty("score").GetInt32());
        var points = await r.Points();
        Assert.Equal(0, points[Mbuthuma].Goals);
        Assert.Equal(1, points[Mokobodi].Goals);
        Assert.Equal(7, points[Mokobodi].Total);
    }
}
