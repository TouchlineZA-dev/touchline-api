using Touchline.Api.Business;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Touchline.Api.Services;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Football;

/// <summary>The single provider-state matrix (runbook Part 1b state list) and the event codes.</summary>
public sealed class MatchStateMatrixTests
{
    [Theory]
    [InlineData(1, MatchStatus.Scheduled, MatchPhase.Scheduled)]
    [InlineData(2, MatchStatus.Live, MatchPhase.FirstHalf)]
    [InlineData(3, MatchStatus.Live, MatchPhase.HalfTime)]
    [InlineData(4, MatchStatus.Live, MatchPhase.Break)]
    [InlineData(5, MatchStatus.Finished, MatchPhase.Finished)]
    [InlineData(6, MatchStatus.Live, MatchPhase.ExtraTime)]
    [InlineData(7, MatchStatus.Finished, MatchPhase.FinishedAfterExtraTime)]
    [InlineData(8, MatchStatus.Finished, MatchPhase.FinishedAfterPenalties)]
    [InlineData(9, MatchStatus.Live, MatchPhase.Penalties)]
    [InlineData(10, MatchStatus.NoResult, MatchPhase.Postponed)]
    [InlineData(11, MatchStatus.NoResult, MatchPhase.Suspended)]
    [InlineData(12, MatchStatus.NoResult, MatchPhase.Cancelled)]
    [InlineData(13, MatchStatus.Scheduled, MatchPhase.ToBeAnnounced)]
    [InlineData(14, MatchStatus.Finished, MatchPhase.WalkOver)]
    [InlineData(15, MatchStatus.NoResult, MatchPhase.Abandoned)]
    [InlineData(16, MatchStatus.Scheduled, MatchPhase.Delayed)]
    [InlineData(17, MatchStatus.Finished, MatchPhase.Awarded)]
    [InlineData(18, MatchStatus.Live, MatchPhase.Interrupted)]
    [InlineData(19, MatchStatus.Scheduled, MatchPhase.AwaitingUpdates)]
    [InlineData(20, MatchStatus.NoResult, MatchPhase.Deleted)]
    [InlineData(21, MatchStatus.Live, MatchPhase.ExtraTimeBreak)]
    [InlineData(22, MatchStatus.Live, MatchPhase.SecondHalf)]
    [InlineData(23, MatchStatus.Live, MatchPhase.ExtraTimeSecondHalf)]
    [InlineData(25, MatchStatus.Live, MatchPhase.PenaltyBreak)]
    [InlineData(26, MatchStatus.Scheduled, MatchPhase.Pending)]
    public void Every_provider_state_is_mapped_explicitly(short stateId, MatchStatus status, MatchPhase phase)
    {
        var state = MatchStateMatrix.For(stateId);
        Assert.Equal(status, state.Status);
        Assert.Equal(phase, state.Phase);
        Assert.NotEqual("UNKNOWN", state.Code);
    }

    [Fact]
    public void The_matrix_covers_all_25_listed_states()
    {
        short[] listed = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 25, 26];
        Assert.Equal(25, MatchStateMatrix.All.Count);
        Assert.Equal(listed, MatchStateMatrix.All.Select(s => s.StateId!.Value).Order());
    }

    [Theory]
    [InlineData(24)]
    [InlineData(99)]
    public void Unknown_codes_are_preserved_and_flagged_never_mapped_to_scheduled(short stateId)
    {
        var state = MatchStateMatrix.For(stateId);
        Assert.Equal(MatchStatus.Unknown, state.Status);
        Assert.Equal(MatchPhase.Unknown, state.Phase);
        Assert.Equal(stateId, state.StateId);
        Assert.True(state.NeedsReview);
        Assert.False(state.Played);
        Assert.False(state.Settled);
    }

    [Fact]
    public void Only_played_states_produce_points_and_only_5_7_8_are_final_played_results()
    {
        Assert.Equal([5, 7, 8], MatchStateMatrix.All.Where(s => MatchStatusExtensions.IsFinal(s.StateId)).Select(s => (int)s.StateId!.Value).Order());
        Assert.False(MatchFactsDecoder.Counts(17));   // awarded: result stands, nobody scores
        Assert.False(MatchFactsDecoder.Counts(14));
        Assert.True(MatchFactsDecoder.Counts(18));    // interrupted mid-match still produces provisional points
        Assert.All(Codes.MatchStates.Live, id => Assert.Equal(MatchStatus.Live, MatchStatusExtensions.FromStateId(id)));
    }

    [Fact]
    public void Awarded_and_postponed_fixtures_are_settled_for_finalising_but_suspended_is_not()
    {
        Assert.True(GameweekRules.CanFinalise(false, [5, 17]));
        Assert.True(GameweekRules.CanFinalise(false, [5, 10]));
        Assert.False(GameweekRules.CanFinalise(false, [5, 11]));
        Assert.False(GameweekRules.CanFinalise(false, [5, 99]));
    }

    [Fact]
    public void The_clock_is_shared_half_time_is_45_and_interrupted_pauses_at_the_latest_event()
    {
        var clock = new MatchClock(new FantasyRules());
        var kickoff = new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(new MatchClockReading(45, ClockSource.HalfTime, false), clock.Read(3, kickoff, 90, 44, kickoff.AddMinutes(52)));
        Assert.Equal(new MatchClockReading(30, ClockSource.EstimatedFromKickoff, true), clock.Read(2, kickoff, 90, 12, kickoff.AddMinutes(30)));
        Assert.Equal(new MatchClockReading(67, ClockSource.LatestEvent, false), clock.Read(18, kickoff, 90, 67, kickoff.AddMinutes(100)));
        Assert.Equal(new MatchClockReading(90, ClockSource.Final, false), clock.Read(5, kickoff, 90, 93, kickoff.AddHours(3)));
        Assert.Equal(new MatchClockReading(null, ClockSource.None, false), clock.Read(1, kickoff, 90, null, kickoff.AddMinutes(-5)));
        Assert.Equal("45+2", MatchClock.Display(45, 2));
        Assert.Equal("90", MatchClock.Display(90, null));
    }

    // ---- Event codes (Part 1b): goals 14 + 16, own goal 15 --------------------------------------------

    private static readonly IReadOnlyDictionary<long, DecoderPlayer> Players = new Dictionary<long, DecoderPlayer>
    {
        [100] = new(1, Codes.PositionIds.Attacker),
        [200] = new(2, Codes.PositionIds.Defender),
    };

    private static Fixture FullTime() => new() { Id = 7, StateId = 5, HomeTeamId = 10, AwayTeamId = 20, HomeScore = 1, AwayScore = 1, Length = 90 };

    [Fact]
    public void Penalty_goal_16_scores_plus_three_and_own_goal_15_scores_minus_two()
    {
        MatchEvent[] events =
        [
            new() { Id = 1, SportmonksEventId = 1, FixtureId = 7, TeamId = 10, TypeId = 16, SportmonksPlayerId = 100, Minute = 30 },
            new() { Id = 2, SportmonksEventId = 2, FixtureId = 7, TeamId = 20, TypeId = 15, SportmonksPlayerId = 200, Minute = 60 },
        ];
        var facts = MatchFactsDecoder.Decode(FullTime(), [], events, Players, 90, 11, true);
        var scoring = new PlayerScoring(new FantasyRules());

        var penaltyTaker = scoring.ScoreFixture(facts.Single(p => p.PlayerId == 1).Facts, Position.ATT);
        Assert.Equal(1, penaltyTaker.Goals);
        Assert.Equal(3, penaltyTaker.GoalPoints);
        Assert.Equal(3, penaltyTaker.Total);

        var ownGoal = scoring.ScoreFixture(facts.Single(p => p.PlayerId == 2).Facts, Position.DEF);
        Assert.Equal(1, ownGoal.OwnGoals);
        Assert.Equal(-2, ownGoal.OwnGoalPoints);
        Assert.Equal(-2, ownGoal.Total);
        Assert.Equal(0, ownGoal.Goals);
    }

    [Fact]
    public void Event_names_follow_the_sportmonks_codes()
    {
        Assert.Equal(("goal", "assist"), MatchDetailService.EventType(14));
        Assert.Equal(("own_goal", null), MatchDetailService.EventType(15));
        Assert.Equal(("penalty_goal", "assist"), MatchDetailService.EventType(16));
        Assert.Equal(("penalty_missed", null), MatchDetailService.EventType(17));
        Assert.Equal(("substitution", "player_off"), MatchDetailService.EventType(18));
        Assert.Equal(("second_yellow", null), MatchDetailService.EventType(21));
    }

    [Fact]
    public void Null_and_unknown_player_ids_never_crash_and_score_for_nobody()
    {
        MatchEvent[] events =
        [
            new() { Id = 1, SportmonksEventId = 1, FixtureId = 7, TeamId = 10, TypeId = 14, SportmonksPlayerId = null, Minute = 10 },
            new() { Id = 2, SportmonksEventId = 2, FixtureId = 7, TeamId = 10, TypeId = 19, SportmonksPlayerId = 999, Minute = 20 },
            new() { Id = 3, SportmonksEventId = 3, FixtureId = 7, TeamId = 10, TypeId = 18, SportmonksPlayerId = null, SportmonksRelatedPlayerId = 100, Minute = 60 },
        ];
        PlayerLineup[] lineups =
        [
            new() { FixtureId = 7, TeamId = 10, SportmonksPlayerId = null, TypeId = 11 },
            new() { FixtureId = 7, TeamId = 10, SportmonksPlayerId = 100, TypeId = 11 },
        ];
        var facts = MatchFactsDecoder.Decode(FullTime(), lineups, events, Players, 90, 11, true);
        var only = Assert.Single(facts);
        Assert.Equal(1, only.PlayerId);
        Assert.Equal(60, only.Facts.WentOffMinute);
        Assert.Equal(0, only.Facts.Goals);
    }

    [Fact]
    public void Revision_ignores_timestamps_but_changes_on_any_correction()
    {
        var fixture = FullTime();
        MatchEvent Goal(long player, bool? rescinded = null, DateTimeOffset? at = null) =>
            new() { Id = 1, SportmonksEventId = 55, FixtureId = 7, TypeId = 14, SportmonksPlayerId = player, Minute = 24, Rescinded = rescinded, UpdatedAt = at ?? default };

        var r1 = MatchDetailService.Revision(fixture, [Goal(100)], []);
        Assert.Equal(r1, MatchDetailService.Revision(fixture, [Goal(100, at: DateTimeOffset.UnixEpoch)], []));
        Assert.NotEqual(r1, MatchDetailService.Revision(fixture, [Goal(200)], []));
        Assert.NotEqual(r1, MatchDetailService.Revision(fixture, [Goal(100, rescinded: true)], []));
        Assert.NotEqual(r1, MatchDetailService.Revision(fixture, [], []));
        Assert.StartsWith("r-", r1);
    }
}
