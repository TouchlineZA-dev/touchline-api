using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Fantasy;

/// <summary>
/// Tests added by the 2026-11-23 audit (docs/audit/FANTASY_AUDIT_2026-11-23.md) for rules that were
/// implemented but not proven by a test.
/// </summary>
public class MatchFactsDecoderTests
{
    private const int Home = 10, Away = 20;
    private const short Xi = 11, Bench = 12;

    private static readonly Dictionary<long, DecoderPlayer> Players = new()
    {
        [1001] = new(1, Codes.PositionIds.Goalkeeper),
        [1002] = new(2, Codes.PositionIds.Defender),
        [1003] = new(3, Codes.PositionIds.Midfielder),
        [1004] = new(4, Codes.PositionIds.Attacker),
        [1005] = new(5, Codes.PositionIds.Midfielder),
        [1006] = new(6, Codes.PositionIds.Attacker),
        [2001] = new(7, Codes.PositionIds.Goalkeeper),
    };

    private static Fixture Finished(int id = 1, short homeScore = 2, short awayScore = 0, short state = Codes.MatchStates.FullTime) =>
        new() { Id = id, StateId = state, HomeTeamId = Home, AwayTeamId = Away, HomeScore = homeScore, AwayScore = awayScore };

    private static PlayerLineup Lineup(long sm, short type, int team = Home, int fixture = 1) =>
        new() { FixtureId = fixture, TeamId = team, SportmonksPlayerId = sm, TypeId = type };

    private static MatchEvent Event(short type, int minute, long? player, long? related = null, int team = Home, bool? rescinded = null, int fixture = 1) =>
        new() { FixtureId = fixture, TeamId = team, TypeId = type, Minute = (short)minute, SportmonksPlayerId = player, SportmonksRelatedPlayerId = related, Rescinded = rescinded };

    private static PlayerFixtureFacts FactsFor(IReadOnlyList<PlayerFacts> all, int playerId) => all.Single(f => f.PlayerId == playerId).Facts;

    private static IReadOnlyList<PlayerFacts> Decode(Fixture f, PlayerLineup[] lineups, MatchEvent[] events, int end = 90) =>
        MatchFactsDecoder.Decode(f, lineups, events, Players, end, Xi, comingOnIsPlayerId: true);

    [Fact]
    public void Lineup_type_11_starts_and_12_is_bench()
    {
        var facts = Decode(Finished(), [Lineup(1003, Xi), Lineup(1005, Bench)], []);

        Assert.True(FactsFor(facts, 3).Started);
        Assert.False(FactsFor(facts, 5).Started);
        Assert.Equal(90, PlayerScoring.Minutes(FactsFor(facts, 3)));
        Assert.Equal(0, PlayerScoring.Minutes(FactsFor(facts, 5)));
    }

    [Fact]
    public void Substitution_player_id_comes_on_and_related_player_goes_off()
    {
        var facts = Decode(Finished(), [Lineup(1003, Xi), Lineup(1005, Bench)], [Event(Codes.EventTypes.Substitution, 63, player: 1005, related: 1003)]);

        Assert.Equal(62, PlayerScoring.Minutes(FactsFor(facts, 3)));   // off in minute 63 → 62
        Assert.Equal(28, PlayerScoring.Minutes(FactsFor(facts, 5)));   // on in minute 63 → 90 − 63 + 1
    }

    [Fact]
    public void Player_subbed_on_and_then_sent_off_plays_between_the_two_minutes_and_gets_minus_two()
    {
        var facts = Decode(Finished(), [Lineup(1003, Xi), Lineup(1005, Bench)],
        [
            Event(Codes.EventTypes.Substitution, 55, player: 1005, related: 1003),
            Event(Codes.EventTypes.RedCard, 70, player: 1005),
        ]);

        var sub = FactsFor(facts, 5);
        Assert.Equal(15, PlayerScoring.Minutes(sub)); // on 55, off 70 → 70 − 55
        var points = new PlayerScoring(new FantasyRules()).ScoreFixture(sub, Position.MID);
        Assert.Equal(0, points.Appearance);
        Assert.Equal(-2, points.Total);
    }

    [Fact]
    public void Second_yellow_after_a_yellow_is_minus_two_in_total_and_ends_minutes()
    {
        var facts = Decode(Finished(), [Lineup(1002, Xi)],
        [
            Event(Codes.EventTypes.YellowCard, 20, player: 1002),
            Event(Codes.EventTypes.SecondYellowRed, 75, player: 1002),
        ]);

        var defender = FactsFor(facts, 2);
        Assert.Equal(74, PlayerScoring.Minutes(defender));
        var points = new PlayerScoring(new FantasyRules()).ScoreFixture(defender, Position.DEF);
        Assert.Equal(-2, points.YellowPoints + points.RedPoints);
        Assert.Equal(1 + 2 - 2, points.Total); // appearance (74 min) + clean sheet (team conceded 0, 60+ min) - 2
    }

    [Fact]
    public void Goals_penalties_and_assists_are_counted_and_rescinded_goals_ignored()
    {
        var facts = Decode(Finished(), [Lineup(1004, Xi), Lineup(1003, Xi)],
        [
            Event(Codes.EventTypes.Goal, 12, player: 1004, related: 1003),
            Event(Codes.EventTypes.PenaltyScored, 40, player: 1004),
            Event(Codes.EventTypes.Goal, 80, player: 1004, related: 1003, rescinded: true), // VAR: disallowed
        ]);

        var striker = FactsFor(facts, 4);
        Assert.Equal(1, striker.Goals);
        Assert.Equal(1, striker.PenaltiesScored);
        Assert.Equal(1, FactsFor(facts, 3).Assists);
    }

    [Fact]
    public void Own_goal_is_counted_for_the_player_who_scored_it()
    {
        var facts = Decode(Finished(homeScore: 0, awayScore: 1), [Lineup(1002, Xi)], [Event(Codes.EventTypes.OwnGoal, 30, player: 1002)]);
        Assert.Equal(1, FactsFor(facts, 2).OwnGoals);
    }

    [Fact]
    public void Goals_conceded_come_from_the_other_teams_score()
    {
        var facts = Decode(Finished(homeScore: 2, awayScore: 0), [Lineup(1001, Xi, team: Home), Lineup(2001, Xi, team: Away)], []);

        Assert.Equal(0, FactsFor(facts, 1).GoalsConceded); // home keeper: clean sheet
        Assert.Equal(2, FactsFor(facts, 7).GoalsConceded); // away keeper
    }

    [Fact]
    public void Substitute_missing_from_the_lineup_still_gets_his_minutes_and_team()
    {
        var facts = Decode(Finished(), [Lineup(1003, Xi)], [Event(Codes.EventTypes.Substitution, 80, player: 1006, related: 1003, team: Home)]);

        var sub = FactsFor(facts, 6);
        Assert.Equal(11, PlayerScoring.Minutes(sub)); // on in minute 80 → 90 − 80 + 1
        Assert.Equal(0, sub.GoalsConceded);
    }

    [Fact]
    public void Players_not_in_football_players_are_skipped()
    {
        var facts = Decode(Finished(), [Lineup(9999, Xi), Lineup(1003, Xi)], []);
        Assert.Single(facts);
    }

    [Theory]
    [InlineData(Codes.MatchStates.Postponed)]
    [InlineData(Codes.MatchStates.Cancelled)]
    [InlineData(Codes.MatchStates.Abandoned)]
    [InlineData(Codes.MatchStates.NotStarted)]
    [InlineData(Codes.MatchStates.AwaitingUpdates)]
    public void Fixtures_that_are_not_live_or_final_produce_no_points_even_with_events(short state)
    {
        // An abandoned match can have goals and cards recorded before it stopped.
        var facts = Decode(Finished(state: state), [Lineup(1004, Xi)], [Event(Codes.EventTypes.Goal, 30, player: 1004), Event(Codes.EventTypes.YellowCard, 35, player: 1004)]);
        Assert.Empty(facts);
    }

    [Fact]
    public void Live_fixture_produces_provisional_facts_up_to_the_current_minute()
    {
        var live = Finished(state: Codes.MatchStates.SecondHalf, homeScore: 0, awayScore: 0);
        var facts = Decode(live, [Lineup(1001, Xi)], [], end: 67);

        var keeper = FactsFor(facts, 1);
        Assert.False(keeper.FixtureFinished);
        Assert.Equal(67, PlayerScoring.Minutes(keeper));
        Assert.True(new PlayerScoring(new FantasyRules()).ScoreFixture(keeper, Position.GK).Provisional);
    }

    [Fact]
    public void Double_gameweek_sums_both_fixtures()
    {
        var first = Decode(Finished(id: 1), [Lineup(1004, Xi, fixture: 1)], [Event(Codes.EventTypes.Goal, 10, player: 1004, fixture: 1)]);
        var second = MatchFactsDecoder.Decode(Finished(id: 2), [Lineup(1004, Xi, fixture: 2)], [Event(Codes.EventTypes.Goal, 50, player: 1004, fixture: 2)], Players, 90, Xi, true);

        var total = new PlayerScoring(new FantasyRules()).ScoreGameweek(first.Concat(second).Where(f => f.PlayerId == 4).Select(f => f.Facts), Position.ATT);
        Assert.Equal(2, total.Fixtures);
        Assert.Equal(2, total.Goals);
        Assert.Equal(2 * (2 + 3), total.Total);
    }
}

public class AuditRuleTests
{
    private static readonly FantasyRules Rules = new();

    [Fact]
    public void Captain_and_vice_captain_both_not_playing_score_zero_and_nobody_else_is_promoted()
    {
        var picks = new List<TeamPickPoints>
        {
            new(1, true, true, false, new PointsBreakdown { Total = 0, Minutes = 0 }),
            new(2, true, false, true, new PointsBreakdown { Total = 0, Minutes = 0 }),
        };
        for (var i = 3; i <= 11; i++) picks.Add(new(i, true, false, false, new PointsBreakdown { Total = 2, Minutes = 90 }));
        for (var i = 12; i <= 15; i++) picks.Add(new(i, false, false, false, new PointsBreakdown { Total = 5, Minutes = 90 }));

        var score = new TeamScoring(Rules).Score(picks, chip: null, transferCost: 0);

        Assert.Equal(18, score.Total); // 9 starters × 2; no multiplier moves to anyone else, bench doesn't count
        Assert.All(score.Picks.Where(p => p.PlayerId > 2), p => Assert.True(p.Multiplier <= 1));
    }

    [Theory]
    [InlineData("4.25", "4.5")]
    [InlineData("4.75", "5.0")]
    [InlineData("6.25", "6.5")]
    [InlineData("6.75", "7.0")]
    [InlineData("12.25", "12.5")]
    [InlineData("6.2499", "6.0")]
    [InlineData("6.7499", "6.5")]
    public void Rounding_exactly_on_quarters_goes_up(string raw, string expected)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(decimal.Parse(expected, c), PlayerValuation.RoundToStep(decimal.Parse(raw, c), 0.5m));
    }

    [Fact]
    public void Defender_price_landing_on_a_quarter_rounds_up()
    {
        // DEF 4.0–7.0 at Q = 0.25: 4.0 + 3.0 × 0.25 = 4.75 → 5.0.
        Assert.Equal(5.0m, new PlayerValuation(Rules.Pricing).Price(0.25m, Position.DEF));
    }

    [Theory]
    [InlineData(new short[] { 5, 10 }, true)]   // one postponed: still finalisable
    [InlineData(new short[] { 5, 12, 15 }, true)]
    [InlineData(new short[] { 10 }, false)]     // nothing played
    [InlineData(new short[] { 5, 19 }, false)]  // awaiting updates: wait
    [InlineData(new short[] { 5, 22 }, false)]  // still live
    [InlineData(new short[] { 5, 1 }, false)]   // not started yet
    public void Gameweek_with_a_postponed_fixture_can_still_be_finalised(short[] states, bool expected)
    {
        Assert.Equal(expected, GameweekRules.CanFinalise(markedComplete: false, states.Select(s => (short?)s).ToList()));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, Chips.Wildcard, true)]
    [InlineData(false, Chips.BenchBoost, false)]
    [InlineData(false, null, false)]
    public void Unlimited_free_transfers_before_gameweek_one_or_with_wildcard(bool firstGameweek, string? chip, bool expected)
    {
        Assert.Equal(expected, TransferRules.UnlimitedFreeTransfers(firstGameweek, chip));
    }

    [Fact]
    public void Budget_after_a_first_pick_is_the_budget_minus_current_prices()
    {
        var rules = TestSquad.SquadRules();
        Assert.Equal(7.0m, rules.BudgetAfterFirstPick(TestSquad.Selection(), TestSquad.Players())); // 100.0 - 93.0
    }

    [Fact]
    public void Points_breakdown_json_round_trips_and_has_the_agreed_keys()
    {
        var breakdown = new PointsBreakdown { Fixtures = 1, Minutes = 90, Appearance = 2, Goals = 1, GoalPoints = 3, YellowCards = 1, YellowPoints = -1, Total = 4 };
        var json = PointsBreakdownJson.Write(breakdown, new PickScore(9, 4, 3, true, 12));

        using var doc = JsonDocument.Parse(json);
        foreach (var key in new[] { "minutes", "appearance", "goals", "goalPoints", "yellowPoints", "total", "multiplier", "counted", "contribution" })
        {
            Assert.True(doc.RootElement.TryGetProperty(key, out _), $"missing {key}");
        }
        Assert.Equal(breakdown, PointsBreakdownJson.Read(json, 4));
        Assert.Equal(7, PointsBreakdownJson.Read(null, 7).Total);
    }

    [Fact]
    public async Task Writes_to_football_and_sync_are_refused_before_reaching_the_database()
    {
        var options = new DbContextOptionsBuilder<TouchlineDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=none").Options;

        await using (var db = new TouchlineDbContext(options))
        {
            db.Teams.Add(new Team { Name = "Should never be saved" });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("'football' schema", ex.Message);
        }

        await using (var db = new TouchlineDbContext(options))
        {
            db.SyncJobs.Add(new SyncJob { JobType = "x", Status = "running" });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("'sync' schema", ex.Message);
        }
    }
}
