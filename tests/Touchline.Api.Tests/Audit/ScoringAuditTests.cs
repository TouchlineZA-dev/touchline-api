using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Touchline.Api.Services;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using Touchline.Api.Tests.Reference;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Audit;

/// <summary>Audit area 7: scoring, through the real decoder (lineups and events → facts) and scorer.</summary>
public sealed class ScoringAuditTests
{
    private static readonly FantasyRules Rules = new();
    private static readonly PlayerScoring Scoring = new(Rules);

    private static readonly Dictionary<long, DecoderPlayer> Players = new()
    {
        [100] = new(1, Codes.PositionIds.Attacker),
        [200] = new(2, Codes.PositionIds.Defender),
        [300] = new(3, Codes.PositionIds.Goalkeeper),
        [400] = new(4, Codes.PositionIds.Midfielder),
        [500] = new(5, Codes.PositionIds.Defender),
    };

    private static Fixture Final(int home = 0, int away = 0) => new() { Id = 7, StateId = 5, HomeTeamId = 10, AwayTeamId = 20, HomeScore = (short)home, AwayScore = (short)away, Length = 90 };

    private static PlayerLineup Starter(long sm, int team = 10) => new() { FixtureId = 7, TeamId = team, SportmonksPlayerId = sm, TypeId = 11 };

    private static long _eventId = 1;
    private static MatchEvent Event(short type, long? player, short minute, short? extra = null, long? related = null, int team = 10) =>
        new() { Id = (int)_eventId, SportmonksEventId = _eventId++, FixtureId = 7, TeamId = team, TypeId = type, SportmonksPlayerId = player, SportmonksRelatedPlayerId = related, Minute = minute, ExtraMinute = extra };

    private static PointsBreakdown Score(long sm, Fixture fixture, IEnumerable<PlayerLineup> lineups, IEnumerable<MatchEvent> events)
    {
        var facts = MatchFactsDecoder.Decode(fixture, lineups, events, Players, 90, 11, true);
        var player = facts.Single(p => p.PlayerId == Players[sm].PlayerId);
        return Scoring.ScoreFixture(player.Facts, PositionExtensions.FromSportmonks(Players[sm].PositionId));
    }

    [Fact]
    public void P01_appearance_is_1_for_45_minutes_and_2_for_the_full_match()
    {
        var full = Score(400, Final(), [Starter(400)], []);
        Assert.Equal(90, full.Minutes);
        Assert.Equal(2, full.Appearance);
        var offAt45 = Score(400, Final(), [Starter(400)], [Event(Codes.EventTypes.Substitution, 999, 45, related: 400)]);
        Assert.Equal(45, offAt45.Minutes);
        Assert.Equal(1, offAt45.Appearance);
        var offAt44 = Score(400, Final(), [Starter(400)], [Event(Codes.EventTypes.Substitution, 999, 44, related: 400)]);
        Assert.Equal(0, offAt44.Appearance);
    }

    [Fact]
    public void P02_goal_and_penalty_goal_are_plus_3_assist_plus_2_own_goal_minus_2()
    {
        var goal = Score(100, Final(2, 0), [Starter(100), Starter(400)], [Event(Codes.EventTypes.Goal, 100, 10, related: 400), Event(Codes.EventTypes.PenaltyScored, 100, 70)]);
        Assert.Equal(2, goal.Goals);
        Assert.Equal(6, goal.GoalPoints);
        var assist = Score(400, Final(2, 0), [Starter(100), Starter(400)], [Event(Codes.EventTypes.Goal, 100, 10, related: 400)]);
        Assert.Equal(1, assist.Assists);
        Assert.Equal(2, assist.AssistPoints);
        var ownGoal = Score(200, Final(0, 1), [Starter(200)], [Event(Codes.EventTypes.OwnGoal, 200, 30)]);
        Assert.Equal(-2, ownGoal.OwnGoalPoints);
        Assert.Equal(0, ownGoal.GoalPoints);
    }

    [Fact]
    public void P03_clean_sheet_is_plus_2_for_gk_and_def_with_60_minutes_and_never_for_mid_or_att()
    {
        Fixture cleanSheet = Final(1, 0);
        Assert.Equal(2, Score(300, cleanSheet, [Starter(300)], []).CleanSheetPoints);
        Assert.Equal(2, Score(200, cleanSheet, [Starter(200)], []).CleanSheetPoints);
        Assert.Equal(0, Score(400, cleanSheet, [Starter(400)], []).CleanSheetPoints);
        Assert.Equal(0, Score(100, cleanSheet, [Starter(100)], []).CleanSheetPoints);
        var offAt59 = Score(200, cleanSheet, [Starter(200)], [Event(Codes.EventTypes.Substitution, 999, 59, related: 200)]);
        Assert.Equal(0, offAt59.CleanSheetPoints);
        var offAt60 = Score(200, cleanSheet, [Starter(200)], [Event(Codes.EventTypes.Substitution, 999, 60, related: 200)]);
        Assert.Equal(2, offAt60.CleanSheetPoints);
        Assert.Equal(0, Score(200, Final(1, 1), [Starter(200)], []).CleanSheetPoints);   // conceded
    }

    [Fact]
    public void P04_yellow_minus_1_red_minus_2_second_yellow_minus_2_in_total()
    {
        Assert.Equal(-1, Score(400, Final(), [Starter(400)], [Event(Codes.EventTypes.YellowCard, 400, 20)]).YellowPoints);
        var red = Score(400, Final(), [Starter(400)], [Event(Codes.EventTypes.RedCard, 400, 30)]);
        Assert.Equal(-2, red.RedPoints);
        Assert.Equal(30, red.Minutes);   // a red card at m plays m
        var second = Score(400, Final(), [Starter(400)], [Event(Codes.EventTypes.YellowCard, 400, 20), Event(Codes.EventTypes.SecondYellowRed, 400, 60)]);
        Assert.Equal(-2, second.YellowPoints + second.RedPoints);
    }

    [Fact]
    public void P05_minutes_off_at_m_plays_m_on_at_m_plays_length_minus_m_plus_1_stoppage_counts_as_45_or_90_and_never_over_length()
    {
        var events = new[] { Event(Codes.EventTypes.Substitution, 500, 46, related: 200) };
        Assert.Equal(46, Score(200, Final(), [Starter(200)], events).Minutes);
        Assert.Equal(45, Score(500, Final(), [Starter(200)], events).Minutes);

        var firstHalfStoppage = new[] { Event(Codes.EventTypes.Substitution, 500, 45, extra: 3, related: 200) };
        Assert.Equal(45, Score(200, Final(), [Starter(200)], firstHalfStoppage).Minutes);
        Assert.Equal(45, Score(500, Final(), [Starter(200)], firstHalfStoppage).Minutes);   // plays from 46

        var lateStoppage = new[] { Event(Codes.EventTypes.Substitution, 500, 90, extra: 4, related: 200) };
        Assert.Equal(90, Score(200, Final(), [Starter(200)], lateStoppage).Minutes);
        Assert.True(Score(500, Final(), [Starter(200)], lateStoppage).Minutes is >= 0 and <= 1);
        Assert.True(Score(200, Final(), [Starter(200)], lateStoppage).Minutes <= 90);
    }

    [Fact]
    public void P06_captain_x3_vice_x2_and_x3_only_when_the_captain_plays_zero_all_week_44_and_41()
    {
        var rule = new SeasonRuleTests();
        rule.Captain_plays_zero_vice_scores_six_nine_others_thirty_minus_four_is_44();
        rule.Captain_plays_only_the_second_fixture_of_a_double_week_and_keeps_the_armband_41();
    }

    [Fact]
    public void P07_no_automatic_substitutions_a_starter_who_plays_zero_scores_zero()
    {
        PointsBreakdown P(int total, int minutes) => new() { Total = total, Minutes = minutes, Fixtures = 1 };
        var picks = new List<TeamPickPoints>
        {
            new(1, true, true, false, P(4, 90)), new(2, true, false, true, P(2, 90)),
            new(3, true, false, false, P(0, 0)),                     // didn't play
            new(30, false, false, false, P(9, 90)),                  // bench star
        };
        var score = new TeamScoring(Rules).Score(picks, null, 0);
        Assert.Equal(4 * 3 + 2 * 2, score.Total);
        Assert.False(score.Picks.Single(p => p.PlayerId == 30).Counted);
    }

    [Fact]
    public void P08_a_missing_captain_is_picked_deterministically_from_the_starting_xi()
    {
        var selection = new SquadSelection("4-4-2", Enumerable.Range(1, 11).ToList(), [20, 21, 22, 23], null, null);
        var a = CaptaincyRules.EnsureCaptains(selection, teamId: 42, gameweekId: 9);
        var b = CaptaincyRules.EnsureCaptains(selection with { Starters = selection.Starters.Reverse().ToList() }, teamId: 42, gameweekId: 9);
        Assert.Equal(a.CaptainId, b.CaptainId);
        Assert.Equal(a.ViceCaptainId, b.ViceCaptainId);
        Assert.Contains(a.CaptainId!.Value, selection.Starters);
        Assert.NotEqual(a.CaptainId, a.ViceCaptainId);
    }
}

/// <summary>Audit area 7 on the real GW9 data: the reference totals 65 and 61, and identical results when re-run.</summary>
public sealed class ScoringReferenceAuditTests(Gw9Fixture g) : IClassFixture<Gw9Fixture>
{
    [SkippableTheory]
    [InlineData(0, 65)]
    [InlineData(1, 61)]
    public async Task P09_gw9_reference_totals_are_65_and_61_and_identical_on_every_run(int index, int expected)
    {
        Skip.If(g.SkipReason is not null, g.SkipReason);
        var reference = new ReferenceWeekTests(g);
        await reference.Reference_squad_totals_match_the_hand_calculation(index, expected);
        await reference.Reference_squad_totals_match_the_hand_calculation(index, expected);   // re-run: same total

        // Every player's breakdown is identical between two runs over the same rows.
        async Task<string> Run()
        {
            using var scope = g.Api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
            var fixtures = await db.Fixtures.Where(x => x.SeasonId == g.SeasonId).OrderBy(x => x.Id).ToListAsync();
            var facts = await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync(fixtures, DateTimeOffset.UtcNow);
            var scoring = new PlayerScoring(new FantasyRules());
            return string.Join("|", facts.GroupBy(x => x.PlayerId).OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{scoring.ScoreGameweek(x.Select(y => y.Facts), x.First().Position)}"));
        }
        Assert.Equal(await Run(), await Run());
    }
}

/// <summary>Audit area 7: a withdrawn event (the worker's withdrawn_at) never scores.</summary>
public sealed class WithdrawnEventAuditTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task P10_a_withdrawn_goal_scores_nothing()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddHours(-3), Codes.MatchStates.FullTime, 1, 0);
        var scorer = await f.PlayerAsync("Audit Withdrawn");
        await f.LineupAsync(fixture, f.Home, scorer.SportmonksId, 11);
        var eventId = FootballDbFixture.NextId();
        await f.EventAsync(fixture, eventId, Codes.EventTypes.Goal, f.Home, scorer.SportmonksId, null, 15);
        Assert.Equal(3, await GoalPointsAsync(fixture, scorer.Id));
        await f.Db.ExecAsync("UPDATE football.match_events SET withdrawn_at = now() WHERE sportmonks_event_id = $1", (long)eventId);
        Assert.Equal(0, await GoalPointsAsync(fixture, scorer.Id));
    }

    private async Task<int> GoalPointsAsync(int fixtureId, int playerId)
    {
        using var scope = f.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
        var fixture = await db.Fixtures.SingleAsync(x => x.Id == fixtureId);
        var facts = await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync([fixture], f.Clock.Now);
        var mine = facts.Where(x => x.PlayerId == playerId).Select(x => x.Facts).ToList();
        return mine.Count == 0 ? 0 : new PlayerScoring(new FantasyRules()).ScoreGameweek(mine, Position.ATT).GoalPoints;
    }
}
