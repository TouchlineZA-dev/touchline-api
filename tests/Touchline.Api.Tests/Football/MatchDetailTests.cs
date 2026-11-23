using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Business;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Touchline.Api.Services;
using Touchline.Api.Tests.Db;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Football;

/// <summary>
/// Sprint 1 regressions (03-Leruo-API Stage 1 table, handbook scenarios, runbook Part 1b) through the
/// real HTTP pipeline against canonical Postgres. Football rows are written the way the worker
/// writes them; every read goes through the API.
/// </summary>
public sealed class MatchDetailTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private const short Xi = 11, Bench = 12;

    private async Task<JsonElement> GetJson(string path)
    {
        var response = await f.Api.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<JsonElement> Detail(int fixtureId) => GetJson($"/api/v1/fixtures/{fixtureId}");

    private async Task<JsonElement> ListRow(int fixtureId)
    {
        var list = await GetJson($"/api/v1/fixtures?teamId={f.Home}");
        return list.GetProperty("fixtures").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == fixtureId);
    }

    /// <summary>The scoring input for one fixture, from the same service finalising uses.</summary>
    private async Task<IReadOnlyList<PlayerFacts>> ScoringInput(int fixtureId)
    {
        using var scope = f.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Touchline.Api.Data.TouchlineDbContext>();
        var fixture = db.Fixtures.Single(x => x.Id == fixtureId);
        return await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync([fixture], f.Clock.Now);
    }

    private static int GoalsFor(IReadOnlyList<PlayerFacts> facts, int playerId) =>
        facts.Where(x => x.PlayerId == playerId).Sum(x => x.Facts.Goals + x.Facts.PenaltiesScored);

    [SkippableFact]
    public async Task First_half_goal_is_live_with_the_same_goal_in_list_detail_and_scoring()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var kickoff = f.Clock.Now.AddMinutes(-30);
        var fixture = await f.FixtureAsync(kickoff, Codes.MatchStates.FirstHalf, 1, 0);
        var scorer = await f.PlayerAsync("First Half Scorer");
        await f.LineupAsync(fixture, f.Home, scorer.SportmonksId, Xi);
        await f.EventAsync(fixture, FootballDbFixture.NextId(), Codes.EventTypes.Goal, f.Home, scorer.SportmonksId, null, 23);

        var row = await ListRow(fixture);
        Assert.Equal("Live", row.GetProperty("status").GetString());
        Assert.Equal("FirstHalf", row.GetProperty("phase").GetString());
        Assert.Equal(1, row.GetProperty("homeScore").GetInt32());

        var detail = await Detail(fixture);
        Assert.Equal("FirstHalf", detail.GetProperty("fixture").GetProperty("phase").GetString());
        Assert.Equal(1, detail.GetProperty("fixture").GetProperty("home").GetProperty("score").GetInt32());
        Assert.Equal("EstimatedFromKickoff", detail.GetProperty("clock").GetProperty("source").GetString());
        var goal = detail.GetProperty("events").GetProperty("items").EnumerateArray().Single();
        Assert.Equal("goal", goal.GetProperty("type").GetString());
        Assert.Equal(scorer.Id, goal.GetProperty("player").GetProperty("playerId").GetInt32());

        Assert.Equal(1, GoalsFor(await ScoringInput(fixture), scorer.Id));
    }

    [SkippableFact]
    public async Task Fixture_without_a_lineup_still_opens_with_lineups_pending_and_no_score()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddDays(2), Codes.MatchStates.NotStarted);

        var detail = await Detail(fixture);
        Assert.Equal("pending", detail.GetProperty("coverage").GetProperty("lineups").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("fixture").GetProperty("home").GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("fixture").GetProperty("away").GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ListRow(fixture)).GetProperty("homeScore").ValueKind);
    }

    [SkippableFact]
    public async Task Worker_outage_reads_succeed_marked_stale_with_zero_outbound_requests()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddMinutes(-60), Codes.MatchStates.SecondHalf, 0, 0);
        var lastWrite = f.Clock.Now.AddMinutes(-100);
        await f.BackdateAsync(fixture, lastWrite);

        var detail = await Detail(fixture);
        var freshness = detail.GetProperty("freshness");
        Assert.True(freshness.GetProperty("stale").GetBoolean());
        Assert.True(freshness.GetProperty("inMatchWindow").GetBoolean());
        Assert.Equal(lastWrite, freshness.GetProperty("lastUpdatedUtc").GetDateTimeOffset());
        Assert.Equal(lastWrite, freshness.GetProperty("timestamps").GetProperty("dbCommittedUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, freshness.GetProperty("timestamps").GetProperty("providerTimestampUtc").ValueKind);
        await ListRow(fixture);
        await GetJson("/api/v1/standings");

        Assert.Empty(f.Api.Outbound.Refused);
    }

    [SkippableFact]
    public async Task Fresh_data_in_the_window_is_not_stale()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddMinutes(-60), Codes.MatchStates.SecondHalf, 0, 0);
        await f.BackdateAsync(fixture, f.Clock.Now.AddMinutes(-10));
        Assert.False((await Detail(fixture)).GetProperty("freshness").GetProperty("stale").GetBoolean());
    }

    [SkippableFact]
    public async Task Corrected_scorer_with_unchanged_score_moves_points_exactly_once_and_changes_the_revision()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddHours(-3), Codes.MatchStates.FullTime, 1, 0);
        var oldScorer = await f.PlayerAsync("Old Scorer");
        var newScorer = await f.PlayerAsync("Mokobodi");
        await f.LineupAsync(fixture, f.Home, oldScorer.SportmonksId, Xi);
        await f.LineupAsync(fixture, f.Home, newScorer.SportmonksId, Xi);
        var eventId = FootballDbFixture.NextId();
        await f.EventAsync(fixture, eventId, Codes.EventTypes.Goal, f.Home, oldScorer.SportmonksId, null, 24);

        var before = await Detail(fixture);
        Assert.Equal(1, GoalsFor(await ScoringInput(fixture), oldScorer.Id));

        // Same event id, re-attributed (replay step 4a).
        await f.EventAsync(fixture, eventId, Codes.EventTypes.Goal, f.Home, newScorer.SportmonksId, null, 24);

        var after = await Detail(fixture);
        Assert.NotEqual(before.GetProperty("revision").GetString(), after.GetProperty("revision").GetString());
        Assert.Equal(1, after.GetProperty("fixture").GetProperty("home").GetProperty("score").GetInt32());
        var goal = after.GetProperty("events").GetProperty("items").EnumerateArray().Single();
        Assert.Equal(newScorer.Id, goal.GetProperty("player").GetProperty("playerId").GetInt32());

        var facts = await ScoringInput(fixture);
        Assert.Equal(0, GoalsFor(facts, oldScorer.Id));
        Assert.Equal(1, GoalsFor(facts, newScorer.Id));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reissued_correction_under_a_new_id_never_double_counts(bool softRescind)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddHours(-3), Codes.MatchStates.FullTime, 1, 0);
        var oldScorer = await f.PlayerAsync("Stale Scorer");
        var newScorer = await f.PlayerAsync("Reissued Scorer");
        var oldId = FootballDbFixture.NextId();
        await f.EventAsync(fixture, oldId, Codes.EventTypes.Goal, f.Home, oldScorer.SportmonksId, null, 24);
        var before = (await Detail(fixture)).GetProperty("revision").GetString();

        // Replay step 4b: the goal comes back under a NEW id; the stale one is rescinded or removed by the worker.
        await f.EventAsync(fixture, FootballDbFixture.NextId(), Codes.EventTypes.Goal, f.Home, newScorer.SportmonksId, null, 24);
        if (softRescind) await f.EventAsync(fixture, oldId, Codes.EventTypes.Goal, f.Home, oldScorer.SportmonksId, null, 24, rescinded: true);
        else await f.Db.ExecAsync("DELETE FROM football.match_events WHERE sportmonks_event_id = $1", (long)oldId);

        var detail = await Detail(fixture);
        Assert.NotEqual(before, detail.GetProperty("revision").GetString());
        Assert.Single(detail.GetProperty("events").GetProperty("items").EnumerateArray());
        var facts = await ScoringInput(fixture);
        Assert.Equal(0, GoalsFor(facts, oldScorer.Id));
        Assert.Equal(1, GoalsFor(facts, newScorer.Id));
    }

    [SkippableFact]
    public async Task Same_snapshot_ingested_three_times_is_one_event_set_and_points_once()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddHours(-3), Codes.MatchStates.FullTime, 1, 0);
        var scorer = await f.PlayerAsync("Thrice Scorer");
        await f.LineupAsync(fixture, f.Home, scorer.SportmonksId, Xi);
        var eventId = FootballDbFixture.NextId();

        var revisions = new List<string?>();
        for (var i = 0; i < 3; i++)
        {
            await f.EventAsync(fixture, eventId, Codes.EventTypes.Goal, f.Home, scorer.SportmonksId, null, 50);
            var detail = await Detail(fixture);
            Assert.Equal(1, detail.GetProperty("events").GetProperty("total").GetInt32());
            revisions.Add(detail.GetProperty("revision").GetString());
        }
        Assert.Single(revisions.Distinct());
        Assert.Equal(1, GoalsFor(await ScoringInput(fixture), scorer.Id));
    }

    [SkippableFact]
    public async Task Phone_reopening_at_half_time_sees_the_same_score_and_timeline()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var kickoff = f.Clock.Now.AddMinutes(-50);
        var fixture = await f.FixtureAsync(kickoff, Codes.MatchStates.HalfTime, 1, 1);
        var a = await f.PlayerAsync("HT Home");
        var b = await f.PlayerAsync("HT Away");
        await f.EventAsync(fixture, FootballDbFixture.NextId(), Codes.EventTypes.Goal, f.Home, a.SportmonksId, null, 12);
        await f.EventAsync(fixture, FootballDbFixture.NextId(), Codes.EventTypes.Goal, f.Away, b.SportmonksId, null, 45, extra: 2);

        var connected = await Detail(fixture);
        f.Clock.Now = f.Clock.Now.AddMinutes(8);
        var reopened = await Detail(fixture);
        f.Clock.Now = f.Clock.Now.AddMinutes(-8);

        Assert.Equal(connected.GetProperty("revision").GetString(), reopened.GetProperty("revision").GetString());
        Assert.Equal(connected.GetProperty("events").GetRawText(), reopened.GetProperty("events").GetRawText());
        Assert.Equal(45, reopened.GetProperty("clock").GetProperty("minute").GetInt32());
        Assert.Equal("HalfTime", reopened.GetProperty("clock").GetProperty("source").GetString());
        Assert.Equal("45+2", reopened.GetProperty("events").GetProperty("items")[1].GetProperty("display").GetString());
    }

    [SkippableFact]
    public async Task Unattributed_goal_and_card_count_for_the_team_but_score_for_nobody()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddHours(-3), Codes.MatchStates.FullTime, 1, 0);
        var keeper = await f.PlayerAsync("Away Keeper", 24);
        await f.LineupAsync(fixture, f.Away, keeper.SportmonksId, Xi);
        await f.LineupAsync(fixture, f.Home, null, Xi, "No Id Player");                       // lineup row without a player id
        await f.EventAsync(fixture, FootballDbFixture.NextId(), Codes.EventTypes.Goal, f.Home, null, null, 30);          // no scorer
        await f.EventAsync(fixture, FootballDbFixture.NextId(), Codes.EventTypes.YellowCard, f.Home, 987654321, null, 40); // unknown player

        var detail = await Detail(fixture);
        Assert.Equal(2, detail.GetProperty("coverage").GetProperty("unattributedEvents").GetInt32());
        Assert.Equal(1, detail.GetProperty("fixture").GetProperty("home").GetProperty("score").GetInt32());
        Assert.False(detail.GetProperty("events").GetProperty("items")[1].GetProperty("player").GetProperty("resolved").GetBoolean());

        var facts = await ScoringInput(fixture);
        var keeperFacts = facts.Single(x => x.PlayerId == keeper.Id).Facts;
        Assert.Equal(1, keeperFacts.GoalsConceded);   // the unattributed goal still counts against the clean sheet
        Assert.DoesNotContain(facts, x => x.Facts.Goals > 0);
    }

    [SkippableFact]
    public async Task Postponed_fixture_shows_its_state_and_has_no_score()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddDays(-1), Codes.MatchStates.Postponed);
        var detail = await Detail(fixture);
        Assert.Equal("NoResult", detail.GetProperty("fixture").GetProperty("status").GetString());
        Assert.Equal("Postponed", detail.GetProperty("fixture").GetProperty("phase").GetString());
        Assert.Equal("not_applicable", detail.GetProperty("coverage").GetProperty("lineups").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("fixture").GetProperty("home").GetProperty("score").ValueKind);
    }

    [SkippableFact]
    public async Task Fixture_moved_to_another_week_follows_the_assignment_and_history_records_it_and_locked_weeks_need_a_decision()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var gw1 = await f.GameweekAsync("Move GW A", new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 7), f.Clock.Now.AddDays(-1)); // locked
        var gw2 = await f.GameweekAsync("Move GW B", new DateOnly(2027, 3, 8), new DateOnly(2027, 3, 14), f.Clock.Now.AddDays(10));
        var gw3 = await f.GameweekAsync("Move GW C", new DateOnly(2027, 3, 15), new DateOnly(2027, 3, 21), f.Clock.Now.AddDays(17));
        var fixture = await f.FixtureAsync(new DateTimeOffset(2027, 3, 3, 15, 0, 0, TimeSpan.Zero), Codes.MatchStates.Postponed);
        var admin = await f.Db.ScalarAsync<int>("INSERT INTO app.users (email, username) VALUES ($1, $1) RETURNING id", $"admin-{Guid.NewGuid():N}");

        using var scope = f.Api.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GameweekService>();

        // No persisted row yet: it sits in GW A by its date (legacy fallback), which is locked.
        var calendar = await service.GetCalendarAsync(f.SeasonId);
        Assert.Equal(gw1, calendar.ForFixture(fixture)!.Id);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.AssignFixtureAsync(fixture, new AssignFixtureRequest(gw2, "Rescheduled to 10 March"), admin, f.Clock.Now, default));

        var moved = await service.AssignFixtureAsync(fixture, new AssignFixtureRequest(gw2, "Rescheduled to 10 March", ConfirmLockedChange: true), admin, f.Clock.Now, default);
        Assert.True(moved.AffectsLockedGameweek);
        Assert.Equal(gw2, (await service.GetCalendarAsync(f.SeasonId)).ForFixture(fixture)!.Id);

        // Neither week locked now: no confirmation needed.
        await service.AssignFixtureAsync(fixture, new AssignFixtureRequest(gw3, "Moved again"), admin, f.Clock.Now, default);
        calendar = await service.GetCalendarAsync(f.SeasonId);
        Assert.Equal(gw3, calendar.ForFixture(fixture)!.Id);
        Assert.Equal(AssignmentSource.Persisted, calendar.AssignmentSources![fixture]);

        var history = await service.HistoryAsync(fixture, default);
        Assert.Equal([gw3, gw2], history.Select(h => h.GameweekId!.Value));
        Assert.StartsWith("[locked gameweek change confirmed]", history[1].Reason);
        Assert.All(history, h => Assert.Equal(admin, h.AssignedByUserId));
    }

    [SkippableFact]
    public async Task Fixture_list_filters_by_club_and_by_south_african_date()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var other = await f.TeamAsync("Filter Club", "FC");
        var third = await f.TeamAsync("Third Club", "TC");
        // 23:30 UTC on 9 May is 01:30 SAST on 10 May.
        var lateNight = await f.FixtureAsync(new DateTimeOffset(2027, 5, 9, 23, 30, 0, TimeSpan.Zero), 1, home: other, away: third);

        var byClub = await GetJson($"/api/v1/fixtures?teamId={other}");
        Assert.Equal([lateNight], byClub.GetProperty("fixtures").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));

        var tenth = await GetJson("/api/v1/fixtures?date=2027-05-10");
        Assert.Contains(lateNight, tenth.GetProperty("fixtures").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        var ninth = await GetJson("/api/v1/fixtures?date=2027-05-09");
        Assert.DoesNotContain(lateNight, ninth.GetProperty("fixtures").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
    }

    [SkippableFact]
    public async Task Unknown_fixture_is_404()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Api.CreateClient().GetAsync("/api/v1/fixtures/987654")).StatusCode);
    }
}
