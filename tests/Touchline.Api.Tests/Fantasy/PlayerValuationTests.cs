using Touchline.Api.Business;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Tests.Fantasy;

public class PlayerValuationTests
{
    private static readonly PricingRules Pricing = new();
    private readonly PlayerValuation _valuation = new(Pricing);

    [Fact]
    public void Worked_example_gives_E_146_7_and_price_11_5()
    {
        // Midfielder, R1 = 6, R2 = 5, R3 = 4 (already adjusted), M = 2400.
        var e = _valuation.ExpectedPoints([6m, 5m, 4m], expectedMinutes: 2400m);
        Assert.Equal(146.7m, Math.Round(e, 1));

        // At Q = 0.90: 4.5 + (12.0 - 4.5) × 0.90 = 11.25, rounds up to 11.5.
        Assert.Equal(11.5m, _valuation.Price(0.90m, Position.MID));
    }

    [Fact]
    public void Points_per_90()
    {
        Assert.Equal(6m, PlayerValuation.PointsPer90(points: 120m, minutes: 1800));
        Assert.Null(PlayerValuation.PointsPer90(10m, 0));
    }

    [Fact]
    public void Weights_rescale_with_two_seasons()
    {
        var w = _valuation.RescaledWeights([true, true, false]);
        Assert.Equal(0.667m, Math.Round(w[0], 3));
        Assert.Equal(0.333m, Math.Round(w[1], 3));
        Assert.Equal(0m, w[2]);
        Assert.Equal(1m, Math.Round(w.Sum(), 10));
    }

    [Fact]
    public void Weights_rescale_with_one_season()
    {
        Assert.Equal([1m, 0m, 0m], _valuation.RescaledWeights([true, false, false]));
        Assert.Equal([0m, 0m, 1m], _valuation.RescaledWeights([false, false, true]));
    }

    [Fact]
    public void Expected_points_with_two_seasons_uses_the_rescaled_weights()
    {
        // (0.6×6 + 0.3×3) ÷ 0.9 = 5.0 per 90; × 1800 ÷ 90 = 100.
        Assert.Equal(100m, Math.Round(_valuation.ExpectedPoints([6m, 3m, null], 1800m), 6));
    }

    [Fact]
    public void Small_sample_is_pulled_close_to_the_position_average()
    {
        // 90 minutes at a huge 30 points per 90, position average 4.
        var adjusted = PlayerValuation.Shrink(r: 30m, minutes: 90, positionAverage: 4m, k: 900m);
        Assert.InRange(adjusted, 4m, 7m);
    }

    [Fact]
    public void Big_sample_barely_moves()
    {
        var adjusted = PlayerValuation.Shrink(r: 6m, minutes: 2700, positionAverage: 4m, k: 900m);
        Assert.InRange(adjusted, 5.4m, 6m);
    }

    [Fact]
    public void Position_average_is_weighted_by_minutes()
    {
        // 2700 min at 6.0 per 90 (180 pts) and 900 min at 2.0 per 90 (20 pts): 200 × 90 ÷ 3600 = 5.0.
        Assert.Equal(5m, PlayerValuation.PositionAverage([new(180m, 2700), new(20m, 900), new(0m, 0)]));
    }

    [Fact]
    public void Percentiles_lowest_zero_highest_one_ties_share()
    {
        var q = PlayerValuation.Percentiles([(1, 10m), (2, 50m), (3, 50m), (4, 90m), (5, 30m)]);

        Assert.Equal(0m, q[1]);
        Assert.Equal(1m, q[4]);
        Assert.Equal(q[2], q[3]);
        Assert.Equal(0.5m, q[2]); // two players below → rank 3 → (3-1)/(5-1)
        Assert.Equal(0.25m, q[5]);
    }

    [Fact]
    public void Lone_player_gets_percentile_half()
    {
        Assert.Equal(0.5m, PlayerValuation.Percentiles([(7, 12m)])[7]);
    }

    [Theory]
    [InlineData("11.25", "11.5")]
    [InlineData("11.24", "11.0")]
    [InlineData("11.75", "12.0")]
    [InlineData("4.0", "4.0")]
    public void Prices_round_to_half_a_million_with_halves_up(string raw, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            PlayerValuation.RoundToStep(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), 0.5m));
    }

    [Fact]
    public void Every_formula_price_stays_inside_its_position_range()
    {
        var random = new Random(3);
        var inputs = Enumerable.Range(1, 200).Select(id => new ValuationInput(
            id,
            PositionNames.Playable[id % 4],
            [new SeasonRecord(random.Next(0, 250), random.Next(1, 3000)), random.Next(2) == 0 ? null : new SeasonRecord(random.Next(0, 200), random.Next(1, 3000))],
            ExpectedMinutes: random.Next(0, 2700))).ToList();

        foreach (var result in _valuation.PriceAll(inputs))
        {
            var range = Pricing.RangeFor(result.Position);
            Assert.InRange(result.Price, range.Min, range.Max);
            Assert.Equal(0m, result.Price % 0.5m);
        }
    }

    [Fact]
    public void Lowest_and_highest_in_a_position_get_the_min_and_max_price()
    {
        var results = _valuation.PriceAll(
        [
            new ValuationInput(1, Position.ATT, [new SeasonRecord(30m, 2700)], 2700m),
            new ValuationInput(2, Position.ATT, [new SeasonRecord(150m, 2700)], 2700m),
            new ValuationInput(3, Position.ATT, [new SeasonRecord(90m, 2700)], 2700m),
        ]);

        Assert.Equal(4.5m, results.Single(r => r.PlayerId == 1).Price);
        Assert.Equal(12.5m, results.Single(r => r.PlayerId == 2).Price);
        Assert.Equal(8.5m, results.Single(r => r.PlayerId == 3).Price); // Q 0.5: 4.5 + 8 × 0.5
    }

    [Fact]
    public void New_player_with_no_history_gets_the_position_minimum()
    {
        var results = _valuation.PriceAll(
        [
            new ValuationInput(1, Position.MID, [null, null, null], 0m),
            new ValuationInput(2, Position.MID, [new SeasonRecord(100m, 2000)], 2000m),
            new ValuationInput(3, Position.GK, [new SeasonRecord(0m, 0)], 0m),
        ]);

        var fresh = results.Single(r => r.PlayerId == 1);
        Assert.Equal(4.5m, fresh.Price);
        Assert.True(fresh.FallbackPrice);
        Assert.Null(fresh.ExpectedPoints);

        var zeroMinutes = results.Single(r => r.PlayerId == 3);
        Assert.Equal(4.0m, zeroMinutes.Price);
        Assert.True(zeroMinutes.FallbackPrice);
    }

    [Fact]
    public void Price_override_wins_over_the_formula()
    {
        var results = _valuation.PriceAll(
        [
            new ValuationInput(1, Position.MID, [null], 0m, new PriceOverride(null, 9.0m)),
            new ValuationInput(2, Position.MID, [new SeasonRecord(200m, 2700)], 2700m, new PriceOverride(null, 6.0m)),
        ]);

        Assert.Equal(9.0m, results.Single(r => r.PlayerId == 1).Price);
        Assert.False(results.Single(r => r.PlayerId == 1).FallbackPrice);
        Assert.Equal(6.0m, results.Single(r => r.PlayerId == 2).Price);
        Assert.True(results.Single(r => r.PlayerId == 2).PriceOverridden);
    }

    [Fact]
    public void Expected_minutes_override_replaces_M()
    {
        var results = _valuation.PriceAll(
        [
            new ValuationInput(1, Position.DEF, [new SeasonRecord(120m, 1800)], 1800m, new PriceOverride(900, null)),
        ]);

        var r = results.Single();
        Assert.Equal(900m, r.ExpectedMinutes);
        Assert.True(r.MinutesOverridden);
        Assert.Equal(6m * 900m / 90m, r.ExpectedPoints); // a lone player's R_adj equals the position average (6.0)
    }

    [Fact]
    public void Pre_season_minutes_are_capped_at_league_matches_times_ninety()
    {
        Assert.Equal(2700m, PlayerValuation.PreSeasonExpectedMinutes(lastSeasonMinutes: 3100, leagueMatchesPerTeam: 30));
        Assert.Equal(1500m, PlayerValuation.PreSeasonExpectedMinutes(1500, 30));
    }

    [Fact]
    public void Mid_season_minutes_from_this_season_or_last_season_below_the_threshold()
    {
        // 900 minutes in 12 available matches, 15 left: 75 × 15.
        Assert.Equal(1125m, _valuation.MidSeasonExpectedMinutes(900, 12, 15, lastSeasonExpectedMinutes: 2400m, leagueMatchesPerTeamLastSeason: 30));
        // Under 450 minutes: last season's 2400 scaled to 15 of 30 matches.
        Assert.Equal(1200m, _valuation.MidSeasonExpectedMinutes(300, 12, 15, 2400m, 30));
    }

    [Fact]
    public void Mid_season_run_uses_the_current_season_as_R1()
    {
        int[] seasonsOldestFirst = [101, 102, 103, 104];

        Assert.Equal([104, 103, 102], _valuation.HistorySeasons(seasonsOldestFirst, targetSeasonId: 104, midSeason: true));
        Assert.Equal([103, 102, 101], _valuation.HistorySeasons(seasonsOldestFirst, targetSeasonId: 104, midSeason: false));
        Assert.Equal([101], _valuation.HistorySeasons(seasonsOldestFirst, targetSeasonId: 102, midSeason: false));
    }

    [Fact]
    public void Seasons_without_data_are_skipped_and_older_ones_used_instead()
    {
        int[] seasonsOldestFirst = [101, 102, 103, 104, 105];
        var candidates = PlayerValuation.CandidateSeasons(seasonsOldestFirst, targetSeasonId: 105, midSeason: false);
        Assert.Equal([104, 103, 102, 101], candidates);

        // 104 has results but no lineups (like PSL 2025/26): S1 becomes 103.
        Assert.Equal([103, 102, 101], _valuation.SeasonsWithData(candidates, id => id != 104));
        Assert.Equal([104, 103, 102], _valuation.SeasonsWithData(candidates, _ => true));
        Assert.Empty(_valuation.SeasonsWithData(candidates, _ => false));
    }

    [Fact]
    public void Recalculating_the_same_window_twice_is_refused_without_force()
    {
        var now = DateTimeOffset.UtcNow;
        var done = now.AddDays(-1);

        Assert.False(PlayerValuation.CanRecalculate(done, force: false, preSeason: false, null, now).IsValid);
        Assert.True(PlayerValuation.CanRecalculate(done, force: true, preSeason: false, null, now).IsValid);
        Assert.True(PlayerValuation.CanRecalculate(null, force: false, preSeason: false, null, now).IsValid);
    }

    [Fact]
    public void Pre_season_run_is_refused_after_the_gameweek_one_deadline()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(PlayerValuation.CanRecalculate(null, false, preSeason: true, gameweekOneDeadline: now.AddHours(-1), now).IsValid);
        Assert.True(PlayerValuation.CanRecalculate(null, false, preSeason: true, gameweekOneDeadline: now.AddHours(1), now).IsValid);
    }

    [Fact]
    public void Price_scale_multiplies_the_ranges()
    {
        var tripled = new PlayerValuation(new PricingRules { PriceScale = 3.0m });
        var range = new PricingRules { PriceScale = 3.0m }.RangeFor(Position.MID);

        Assert.Equal(13.5m, range.Min);
        Assert.Equal(36.0m, range.Max);
        Assert.Equal(13.5m, tripled.Price(0m, Position.MID));
        Assert.Equal(36.0m, tripled.Price(1m, Position.MID));
    }

    [Fact]
    public void Default_budget_and_price_scale_agree()
    {
        var rules = new FantasyRules();
        Assert.Equal(100.0m, rules.Budget);
        Assert.Equal(1.0m, rules.Pricing.PriceScale);
    }

    [Fact]
    public void Backtest_reports_bands_and_correlation()
    {
        var report = PlayerValuation.Backtest(Position.MID, [(4.5m, 20m), (5.0m, 30m), (8.0m, 80m), (11.5m, 140m)]);

        Assert.Equal(4, report.Players);
        Assert.Equal(20m, report.Bands.Single(b => b.From == 4m).AverageRealPoints);   // 4.0–5.0: the 4.5 player
        Assert.Equal(30m, report.Bands.Single(b => b.From == 5m).AverageRealPoints);   // 5.0–6.0: the 5.0 player
        Assert.True(report.PriceVsPointsCorrelation > 0.9);
    }
}
