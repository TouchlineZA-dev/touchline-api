using Touchline.Api.Business;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Tests.Fantasy;

public class ScoringTests
{
    private static readonly FantasyRules Rules = new();
    private readonly PlayerScoring _scoring = new(Rules);

    private static PlayerFixtureFacts Starter(int? wentOff = null, bool finished = true, int end = 90) =>
        new() { FixtureId = 1, Started = true, WentOffMinute = wentOff, MatchEndMinute = end, FixtureFinished = finished, GoalsConceded = 1 };

    private static PlayerFixtureFacts Sub(int cameOn, int? wentOff = null, bool finished = true, int end = 90) =>
        new() { FixtureId = 1, CameOnMinute = cameOn, WentOffMinute = wentOff, MatchEndMinute = end, FixtureFinished = finished, GoalsConceded = 1 };

    // ---- Minutes ------------------------------------------------------------------------------

    [Fact]
    public void Starter_who_plays_on_gets_the_full_match()
    {
        Assert.Equal(90, PlayerScoring.Minutes(Starter()));
        Assert.True(PlayerScoring.PlayedFullMatch(Starter()));
    }

    [Fact]
    public void Starter_subbed_off_plays_until_that_minute()
    {
        Assert.Equal(67, PlayerScoring.Minutes(Starter(wentOff: 67)));
        Assert.False(PlayerScoring.PlayedFullMatch(Starter(wentOff: 67)));
    }

    [Fact]
    public void Substitute_plays_from_the_minute_he_came_on()
    {
        Assert.Equal(30, PlayerScoring.Minutes(Sub(cameOn: 60)));
    }

    [Fact]
    public void Substitute_who_is_then_sent_off_plays_between_the_two_minutes()
    {
        Assert.Equal(20, PlayerScoring.Minutes(Sub(cameOn: 60, wentOff: 80)));
    }

    [Fact]
    public void Red_card_ends_minutes_at_the_card()
    {
        var f = Starter(wentOff: 35) with { RedCard = true };
        Assert.Equal(35, PlayerScoring.Minutes(f));
    }

    [Fact]
    public void Unused_substitute_plays_zero_minutes()
    {
        var f = new PlayerFixtureFacts { MatchEndMinute = 90, FixtureFinished = true };
        Assert.Equal(0, PlayerScoring.Minutes(f));
    }

    [Fact]
    public void Fixture_length_is_used_when_set()
    {
        Assert.Equal(120, PlayerScoring.Minutes(Starter(end: 120)));
    }

    [Fact]
    public void Live_match_counts_up_to_the_current_minute_and_is_never_a_full_match_yet()
    {
        var live = Starter(finished: false, end: 63);
        Assert.Equal(63, PlayerScoring.Minutes(live));
        Assert.False(PlayerScoring.PlayedFullMatch(live));
    }

    // ---- Appearance ---------------------------------------------------------------------------

    [Theory]
    [InlineData(30, 0)]
    [InlineData(44, 0)]
    [InlineData(45, 1)]
    [InlineData(89, 1)]
    public void Appearance_points_by_minutes(int minutes, int expected)
    {
        // Came on so that he plays exactly `minutes` until the end.
        var f = Sub(cameOn: 90 - minutes);
        Assert.Equal(minutes, PlayerScoring.Minutes(f));
        Assert.Equal(expected, _scoring.AppearancePoints(f));
    }

    [Fact]
    public void Full_match_gives_two_appearance_points_in_total()
    {
        Assert.Equal(2, _scoring.AppearancePoints(Starter()));
    }

    [Fact]
    public void Starter_subbed_off_at_89_gets_one_appearance_point()
    {
        Assert.Equal(1, _scoring.AppearancePoints(Starter(wentOff: 89)));
    }

    // ---- Goals and assists --------------------------------------------------------------------

    [Fact]
    public void Goals_and_penalties_are_three_points_each()
    {
        var f = Starter() with { Goals = 2, PenaltiesScored = 1 };
        var b = _scoring.ScoreFixture(f, Position.ATT);

        Assert.Equal(3, b.Goals);
        Assert.Equal(9, b.GoalPoints);
        Assert.Equal(11, b.Total); // 2 appearance + 9
    }

    [Fact]
    public void Assists_count_when_enabled_and_not_when_disabled()
    {
        var f = Starter() with { Assists = 2 };
        Assert.Equal(4, _scoring.ScoreFixture(f, Position.MID).AssistPoints);

        var off = new PlayerScoring(new FantasyRules { AssistsEnabled = false });
        var b = off.ScoreFixture(f, Position.MID);
        Assert.Equal(0, b.AssistPoints);
        Assert.Equal(2, b.Total);
    }

    // ---- Clean sheets -------------------------------------------------------------------------

    [Theory]
    [InlineData(59, 0)]
    [InlineData(60, 2)]
    public void Clean_sheet_needs_sixty_minutes(int minutes, int expected)
    {
        var f = Starter(wentOff: minutes) with { GoalsConceded = 0 };
        Assert.Equal(expected, _scoring.ScoreFixture(f, Position.DEF).CleanSheetPoints);
    }

    [Theory]
    [InlineData(Position.GK, 2)]
    [InlineData(Position.DEF, 2)]
    [InlineData(Position.MID, 0)]
    [InlineData(Position.ATT, 0)]
    public void Clean_sheet_only_for_goalkeepers_and_defenders(Position position, int expected)
    {
        var f = Starter() with { GoalsConceded = 0 };
        Assert.Equal(expected, _scoring.ScoreFixture(f, position).CleanSheetPoints);
    }

    [Fact]
    public void No_clean_sheet_if_the_team_conceded()
    {
        var f = Starter() with { GoalsConceded = 1 };
        Assert.Equal(0, _scoring.ScoreFixture(f, Position.GK).CleanSheetPoints);
    }

    [Fact]
    public void Live_clean_sheet_is_provisional()
    {
        var f = Starter(finished: false, end: 70) with { GoalsConceded = 0 };
        var b = _scoring.ScoreFixture(f, Position.GK);

        Assert.Equal(2, b.CleanSheetPoints);
        Assert.True(b.Provisional);
    }

    // ---- Cards and own goals ------------------------------------------------------------------

    [Fact]
    public void Yellow_card_is_minus_one()
    {
        var b = _scoring.ScoreFixture(Starter() with { YellowCards = 1 }, Position.MID);
        Assert.Equal(-1, b.YellowPoints);
        Assert.Equal(1, b.Total);
    }

    [Fact]
    public void Red_card_is_minus_two()
    {
        var b = _scoring.ScoreFixture(Starter(wentOff: 50) with { RedCard = true }, Position.MID);
        Assert.Equal(-2, b.RedPoints);
        Assert.Equal(-1, b.Total); // 1 appearance (50 minutes) - 2
    }

    [Fact]
    public void Second_yellow_counts_as_a_red_only()
    {
        // The data carries a yellow (19) and a second yellow (21): -2 in total, no extra -1.
        var f = Starter(wentOff: 70) with { YellowCards = 1, SecondYellow = true };
        var b = _scoring.ScoreFixture(f, Position.DEF);

        Assert.Equal(0, b.YellowPoints);
        Assert.Equal(-2, b.RedPoints);
        Assert.Equal(-2, b.YellowPoints + b.RedPoints);
    }

    [Fact]
    public void Own_goal_is_minus_two()
    {
        var b = _scoring.ScoreFixture(Starter() with { OwnGoals = 1 }, Position.DEF);
        Assert.Equal(-2, b.OwnGoalPoints);
    }

    // ---- Double gameweeks ---------------------------------------------------------------------

    [Fact]
    public void Double_gameweek_points_add_up()
    {
        var first = Starter() with { FixtureId = 1, Goals = 1 };            // 2 + 3 = 5
        var second = Starter(wentOff: 60) with { FixtureId = 2, YellowCards = 1 }; // 1 - 1 = 0
        var b = _scoring.ScoreGameweek([first, second], Position.ATT);

        Assert.Equal(2, b.Fixtures);
        Assert.Equal(150, b.Minutes);
        Assert.Equal(5, b.Total);
    }

    // ---- Team scoring -------------------------------------------------------------------------

    private static PointsBreakdown Points(int total, int minutes = 90) => new() { Total = total, Minutes = minutes, Fixtures = 1 };

    private static List<TeamPickPoints> Team(int captainPoints, int captainMinutes, int vicePoints, int benchPoints = 3)
    {
        var picks = new List<TeamPickPoints>
        {
            new(1, true, true, false, Points(captainPoints, captainMinutes)),
            new(2, true, false, true, Points(vicePoints)),
        };
        for (var i = 3; i <= 11; i++) picks.Add(new(i, true, false, false, Points(2)));
        for (var i = 12; i <= 15; i++) picks.Add(new(i, false, false, false, Points(benchPoints)));
        return picks;
    }

    private readonly TeamScoring _team = new(Rules);

    [Fact]
    public void Captain_scores_triple_and_vice_captain_double()
    {
        var score = _team.Score(Team(captainPoints: 5, captainMinutes: 90, vicePoints: 4), chip: null, transferCost: 0);
        // 5×3 + 4×2 + 9 starters × 2 = 15 + 8 + 18
        Assert.Equal(41, score.Total);
    }

    [Fact]
    public void Vice_captain_gets_triple_when_the_captain_does_not_play()
    {
        var score = _team.Score(Team(captainPoints: 0, captainMinutes: 0, vicePoints: 4), null, 0);
        Assert.Equal(12 + 18, score.Total);
        Assert.Equal(3, score.Picks.Single(p => p.PlayerId == 2).Multiplier);
    }

    [Fact]
    public void Triple_captain_is_times_four()
    {
        var score = _team.Score(Team(5, 90, 4), Chips.TripleCaptain, 0);
        Assert.Equal(20 + 8 + 18, score.Total);
    }

    [Fact]
    public void Triple_captain_moves_to_the_vice_captain_when_the_captain_does_not_play()
    {
        var score = _team.Score(Team(captainPoints: 0, captainMinutes: 0, vicePoints: 5), Chips.TripleCaptain, 0);
        Assert.Equal(20, score.Picks.Single(p => p.PlayerId == 2).Contribution);
    }

    [Fact]
    public void Bench_never_scores_without_bench_boost()
    {
        var score = _team.Score(Team(5, 90, 4, benchPoints: 6), null, 0);
        Assert.Equal(41, score.Total);
        Assert.Equal(24, score.BenchPoints);
        Assert.All(score.Picks.Where(p => p.PlayerId >= 12), p => Assert.False(p.Counted));
    }

    [Fact]
    public void Bench_boost_counts_the_bench()
    {
        var score = _team.Score(Team(5, 90, 4, benchPoints: 6), Chips.BenchBoost, 0);
        Assert.Equal(41 + 24, score.Total);
    }

    [Fact]
    public void No_auto_subs_a_starter_who_did_not_play_scores_zero_and_the_bench_does_not_replace_him()
    {
        var picks = Team(5, 90, 4, benchPoints: 6);
        picks[5] = picks[5] with { Points = Points(0, minutes: 0) }; // a starter who didn't play

        var score = _team.Score(picks, null, 0);
        Assert.Equal(41 - 2, score.Total);
        Assert.Equal(0, score.Picks.Single(p => p.PlayerId == picks[5].PlayerId).Contribution);
        Assert.All(score.Picks.Where(p => p.PlayerId >= 12), p => Assert.Equal(0, p.Contribution));
    }

    [Fact]
    public void Transfer_hits_are_subtracted()
    {
        var score = _team.Score(Team(5, 90, 4), null, transferCost: 8);
        Assert.Equal(33, score.Total);
        Assert.Equal(41, score.PointsBeforeHits);
    }

    [Fact]
    public void Team_score_is_provisional_while_any_player_is_provisional()
    {
        var picks = Team(5, 90, 4);
        picks[3] = picks[3] with { Points = picks[3].Points with { Provisional = true } };

        Assert.True(_team.Score(picks, null, 0).Provisional);
        Assert.False(_team.Score(Team(5, 90, 4), null, 0).Provisional);
    }

    // ---- Captaincy auto-pick --------------------------------------------------------------------

    [Fact]
    public void Missing_captaincy_is_filled_from_the_starting_xi()
    {
        var s = TestSquad.Selection() with { CaptainId = null, ViceCaptainId = null };
        var filled = CaptaincyRules.EnsureCaptains(s, new Random(1));

        Assert.Contains(filled.CaptainId!.Value, s.Starters);
        Assert.Contains(filled.ViceCaptainId!.Value, s.Starters);
        Assert.NotEqual(filled.CaptainId, filled.ViceCaptainId);
    }

    [Fact]
    public void Existing_captaincy_is_left_alone()
    {
        var s = TestSquad.Selection();
        Assert.Same(s, CaptaincyRules.EnsureCaptains(s, new Random(1)));
    }
}
