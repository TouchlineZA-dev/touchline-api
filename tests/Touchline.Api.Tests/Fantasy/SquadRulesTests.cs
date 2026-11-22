using Touchline.Api.Business;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Tests.Fantasy;

public class SquadRulesTests
{
    private readonly SquadRules _rules = TestSquad.SquadRules();
    private readonly Dictionary<int, FantasyPlayer> _players = TestSquad.Players();

    private RuleCheck Validate(SquadSelection s, decimal? budget = 100.0m) => _rules.ValidateSquad(s, _players, budget);

    private static void AssertFails(RuleCheck check, string expectedMessage)
    {
        Assert.False(check.IsValid);
        Assert.Contains(expectedMessage, check.Errors);
    }

    // ---- Squad --------------------------------------------------------------------------

    [Theory]
    [InlineData("4-3-3")]
    [InlineData("4-4-2")]
    [InlineData("3-5-2")]
    [InlineData("3-4-3")]
    [InlineData("5-3-2")]
    public void Every_allowed_formation_gives_a_valid_xi_and_bench(string formation)
    {
        var selection = TestSquad.Selection(formation);
        var check = Validate(selection);

        Assert.True(check.IsValid, string.Join(" | ", check.Errors));
        Assert.Equal(11, selection.Starters.Count);
        Assert.Equal(4, selection.Bench.Count);
        Assert.Single(selection.Bench, id => _players[id].Position == Position.GK);
    }

    [Fact]
    public void Wrong_position_counts_fail()
    {
        var s = TestSquad.Selection("4-4-2");
        // Swap bench defender 15 for spare midfielder 26: 2 GK, 4 DEF, 6 MID, 3 ATT.
        s = s with { Bench = s.Bench.Select(id => id == 15 ? 26 : id).ToList() };

        var check = Validate(s);
        AssertFails(check, "A squad needs exactly 5 defenders; you have 4.");
        AssertFails(check, "A squad needs exactly 5 midfielders; you have 6.");
    }

    [Fact]
    public void Sixteen_players_fail()
    {
        var s = TestSquad.Selection();
        s = s with { Bench = [.. s.Bench, 16] };
        AssertFails(Validate(s), "A squad needs exactly 15 players; you have 16.");
    }

    [Fact]
    public void Duplicate_player_fails()
    {
        var s = TestSquad.Selection();
        s = s with { Bench = s.Bench.Select(id => id == 15 ? 11 : id).ToList() };
        AssertFails(Validate(s), "Player 11 is in the squad more than once.");
    }

    [Fact]
    public void Four_players_from_one_club_fail()
    {
        // Club 3 already has defenders 11 and 14; move midfielder 21 and attacker 31 there too.
        _players[21] = _players[21] with { ClubId = 3, ClubName = "Kaizer Chiefs" };
        _players[31] = _players[31] with { ClubId = 3, ClubName = "Kaizer Chiefs" };
        _players[11] = _players[11] with { ClubName = "Kaizer Chiefs" };
        _players[14] = _players[14] with { ClubName = "Kaizer Chiefs" };

        AssertFails(Validate(TestSquad.Selection()), "Max 3 players from Kaizer Chiefs; you have 4.");
    }

    [Fact]
    public void Three_players_from_one_club_is_fine()
    {
        _players[21] = _players[21] with { ClubId = 3 };
        Assert.True(Validate(TestSquad.Selection()).IsValid);
    }

    [Fact]
    public void Over_budget_fails()
    {
        foreach (var id in TestSquad.Attackers) _players[id] = _players[id] with { Price = 12.0m };
        // 9.0 GK + 25.0 DEF + 35.0 MID + 36.0 ATT = 105.0
        AssertFails(Validate(TestSquad.Selection()), "The squad costs R105.0m, which is over the R100.0m budget.");
    }

    [Fact]
    public void Exactly_on_budget_passes()
    {
        foreach (var id in TestSquad.Attackers) _players[id] = _players[id] with { Price = 10.0m };
        // 9 + 25 + 35 + 30 = 99; nudge one GK up to hit 100.0 exactly.
        _players[1] = _players[1] with { Price = 5.5m };
        Assert.True(Validate(TestSquad.Selection()).IsValid);
    }

    [Fact]
    public void A_player_without_a_club_is_allowed_and_flagged()
    {
        _players[33] = _players[33] with { ClubId = null, ClubName = null };
        var check = Validate(TestSquad.Selection());

        Assert.True(check.IsValid);
        Assert.Contains("Player 33 has no club yet this season, so he doesn't count towards any club limit.", check.Warnings);
    }

    [Fact]
    public void Unknown_position_cannot_be_picked()
    {
        var s = TestSquad.Selection();
        s = s with { Bench = s.Bench.Select(id => id == 15 ? 99 : id).ToList() };
        AssertFails(Validate(s), "Player 99 has no known position yet, so he can't be picked.");
    }

    [Fact]
    public void Unknown_player_id_fails()
    {
        var s = TestSquad.Selection();
        s = s with { Bench = s.Bench.Select(id => id == 15 ? 12345 : id).ToList() };
        AssertFails(Validate(s), "Player 12345 doesn't exist.");
    }

    // ---- Formation and starting XI --------------------------------------------------------

    [Fact]
    public void Formation_not_in_the_list_fails()
    {
        var s = TestSquad.Selection("4-4-2") with { Formation = "4-5-1" };
        AssertFails(Validate(s), "'4-5-1' isn't an allowed formation. Choose one of 4-3-3, 4-4-2, 3-5-2, 3-4-3, 5-3-2.");
    }

    [Theory]
    [InlineData(4, 5, 1, "The starting XI needs 2 or 3 attackers; you have 1.")]
    [InlineData(5, 4, 1, "The starting XI needs 2 or 3 attackers; you have 1.")]
    [InlineData(5, 2, 3, "The starting XI needs 3 to 5 midfielders; you have 2.")]
    [InlineData(2, 5, 3, "The starting XI needs 3 to 5 defenders; you have 2.")]
    public void Starting_shapes_outside_the_five_formations_fail(int def, int mid, int att, string expected)
    {
        var starters = new List<int> { 1 };
        starters.AddRange(TestSquad.Defenders.Take(def));
        starters.AddRange(TestSquad.Midfielders.Take(mid));
        starters.AddRange(TestSquad.Attackers.Take(att));
        var bench = new List<int> { 2 };
        bench.AddRange(TestSquad.Defenders.Skip(def));
        bench.AddRange(TestSquad.Midfielders.Skip(mid));
        bench.AddRange(TestSquad.Attackers.Skip(att));

        var s = new SquadSelection($"{def}-{mid}-{att}", starters, bench, starters[^1], starters[^2]);
        var check = Validate(s);

        AssertFails(check, expected);
    }

    [Fact]
    public void Two_defenders_fail_even_if_the_formation_says_otherwise()
    {
        // Claims 4-4-2 but starts 2 DEF, 5 MID, 3 ATT.
        var starters = new List<int> { 1, 11, 12, 21, 22, 23, 24, 25, 31, 32, 33 };
        var bench = new List<int> { 2, 13, 14, 15 };
        var check = Validate(new SquadSelection("4-4-2", starters, bench, 21, 22));

        AssertFails(check, "The starting XI needs 3 to 5 defenders; you have 2.");
        AssertFails(check, "Your formation is 4-4-2, but your starting XI has 2 defenders, 5 midfielders and 3 attackers.");
    }

    [Fact]
    public void Starters_must_match_the_chosen_formation()
    {
        var s = TestSquad.Selection("4-4-2") with { Formation = "4-3-3" };
        AssertFails(Validate(s), "Your formation is 4-3-3, but your starting XI has 4 defenders, 4 midfielders and 2 attackers.");
    }

    // ---- Goalkeepers ----------------------------------------------------------------------

    [Fact]
    public void Squad_with_one_goalkeeper_fails()
    {
        var s = TestSquad.Selection();
        s = s with { Bench = s.Bench.Select(id => id == 2 ? 16 : id).ToList() };
        var check = Validate(s);

        AssertFails(check, "A squad needs exactly 2 goalkeepers; you have 1.");
        AssertFails(check, "The bench needs exactly 1 goalkeeper; you have 0.");
    }

    [Fact]
    public void Squad_with_three_goalkeepers_fails()
    {
        var s = TestSquad.Selection();
        s = s with { Bench = s.Bench.Select(id => id == 15 ? 3 : id).ToList() };
        var check = Validate(s);

        AssertFails(check, "A squad needs exactly 2 goalkeepers; you have 3.");
        AssertFails(check, "The bench needs exactly 1 goalkeeper; you have 2.");
    }

    [Fact]
    public void Starting_xi_with_no_goalkeeper_fails()
    {
        // Both keepers on the bench, an extra defender starting.
        var starters = new List<int> { 15, 11, 12, 13, 14, 21, 22, 23, 24, 31, 32 };
        var bench = new List<int> { 1, 2, 25, 33 };
        var check = Validate(new SquadSelection("4-4-2", starters, bench, 21, 11));

        AssertFails(check, "The starting XI needs exactly 1 goalkeeper; you have 0.");
        AssertFails(check, "The bench needs exactly 1 goalkeeper; you have 2.");
    }

    [Fact]
    public void Starting_xi_with_two_goalkeepers_fails()
    {
        var starters = new List<int> { 1, 2, 11, 12, 13, 21, 22, 23, 24, 31, 32 };
        var bench = new List<int> { 14, 15, 25, 33 };
        var check = Validate(new SquadSelection("3-4-2", starters, bench, 21, 11));

        AssertFails(check, "The starting XI needs exactly 1 goalkeeper; you have 2.");
        AssertFails(check, "The bench needs exactly 1 goalkeeper; you have 0.");
    }

    [Fact]
    public void A_goalkeeper_can_be_captain()
    {
        var s = TestSquad.Selection() with { CaptainId = 1 };
        Assert.True(Validate(s).IsValid);
    }

    // ---- Captaincy --------------------------------------------------------------------------

    [Fact]
    public void Captain_on_the_bench_fails()
    {
        var s = TestSquad.Selection("4-4-2") with { CaptainId = 25 }; // 25 is on the bench in 4-4-2
        AssertFails(Validate(s), "The captain must be in the starting XI.");
    }

    [Fact]
    public void Vice_captain_on_the_bench_fails()
    {
        var s = TestSquad.Selection("4-4-2") with { ViceCaptainId = 2 };
        AssertFails(Validate(s), "The vice-captain must be in the starting XI.");
    }

    [Fact]
    public void Captain_and_vice_captain_must_differ()
    {
        var s = TestSquad.Selection() with { ViceCaptainId = 21 };
        AssertFails(Validate(s), "The captain and vice-captain must be different players.");
    }

    [Fact]
    public void Missing_captain_fails()
    {
        var s = TestSquad.Selection() with { CaptainId = null };
        AssertFails(Validate(s), "Pick a captain.");
    }

    [Fact]
    public void Every_broken_rule_is_reported_at_once()
    {
        var s = TestSquad.Selection() with { Formation = "9-9-9", ViceCaptainId = 21 };
        s = s with { Bench = [.. s.Bench, 16] };
        var check = Validate(s);

        Assert.True(check.Errors.Count >= 3, string.Join(" | ", check.Errors));
    }

    // ---- Lineup changes ---------------------------------------------------------------------

    [Fact]
    public void Valid_lineup_swap_is_accepted()
    {
        var current = TestSquad.Selection("4-4-2");
        // Bench midfielder 25 for starting defender 14: becomes 3-5-2.
        var proposed = current with
        {
            Formation = "3-5-2",
            Starters = current.Starters.Select(id => id == 14 ? 25 : id).ToList(),
            Bench = current.Bench.Select(id => id == 25 ? 14 : id).ToList(),
        };

        var check = _rules.ValidateLineupChange(current, proposed, _players);
        Assert.True(check.IsValid, string.Join(" | ", check.Errors));
    }

    [Fact]
    public void Lineup_swap_that_breaks_the_formation_fails()
    {
        var current = TestSquad.Selection("4-4-2");
        // Bench defender 15 for starting attacker 32: 5-4-1, not allowed.
        var proposed = current with
        {
            Formation = "5-4-1",
            Starters = current.Starters.Select(id => id == 32 ? 15 : id).ToList(),
            Bench = current.Bench.Select(id => id == 15 ? 32 : id).ToList(),
        };

        var check = _rules.ValidateLineupChange(current, proposed, _players);
        AssertFails(check, "The starting XI needs 2 or 3 attackers; you have 1.");
    }

    [Fact]
    public void Bench_goalkeeper_swapping_with_an_outfield_player_fails()
    {
        var current = TestSquad.Selection("4-4-2");
        var proposed = current with
        {
            Starters = current.Starters.Select(id => id == 11 ? 2 : id).ToList(),
            Bench = current.Bench.Select(id => id == 2 ? 11 : id).ToList(),
            ViceCaptainId = 12,
        };

        var check = _rules.ValidateLineupChange(current, proposed, _players);
        AssertFails(check, "The bench goalkeeper can only swap with the starting goalkeeper.");
    }

    [Fact]
    public void Bench_goalkeeper_swapping_with_the_starting_goalkeeper_is_fine()
    {
        var current = TestSquad.Selection("4-4-2");
        var proposed = current with
        {
            Starters = current.Starters.Select(id => id == 1 ? 2 : id).ToList(),
            Bench = current.Bench.Select(id => id == 2 ? 1 : id).ToList(),
        };

        Assert.True(_rules.ValidateLineupChange(current, proposed, _players).IsValid);
    }

    [Fact]
    public void Lineup_change_cannot_bring_in_new_players()
    {
        var current = TestSquad.Selection("4-4-2");
        var proposed = current with { Bench = current.Bench.Select(id => id == 15 ? 16 : id).ToList() };

        AssertFails(_rules.ValidateLineupChange(current, proposed, _players),
            "A lineup change can't add or remove players; use transfers for that.");
    }

    // ---- Transfers (squad side) ---------------------------------------------------------------

    [Fact]
    public void Like_for_like_transfer_keeps_two_five_five_three()
    {
        var current = TestSquad.Selection();
        var outcome = _rules.ApplyTransfers(current, [15, 33], [16, 34], _players, budgetRemaining: 7.0m);

        Assert.True(outcome.Check.IsValid, string.Join(" | ", outcome.Check.Errors));
        var squad = outcome.NewSelection!.AllPlayers.Select(id => _players[id].Position).ToList();
        Assert.Equal(2, squad.Count(p => p == Position.GK));
        Assert.Equal(5, squad.Count(p => p == Position.DEF));
        Assert.Equal(5, squad.Count(p => p == Position.MID));
        Assert.Equal(3, squad.Count(p => p == Position.ATT));
        // Sold 5.0 + 8.0, bought 4.5 + 9.0: 7.0 + 13.0 - 13.5 = 6.5 left.
        Assert.Equal(6.5m, outcome.NewBudgetRemaining);
    }

    [Fact]
    public void Transfer_swapping_a_goalkeeper_for_a_defender_fails()
    {
        var outcome = _rules.ApplyTransfers(TestSquad.Selection(), [2], [16], _players, budgetRemaining: 7.0m);

        Assert.False(outcome.Check.IsValid);
        Assert.Contains(outcome.Check.Errors, e => e.StartsWith("You're selling 1 goalkeeper but buying 0."));
        Assert.Null(outcome.NewSelection);
    }

    [Fact]
    public void Transfer_of_a_player_with_unknown_position_fails()
    {
        var outcome = _rules.ApplyTransfers(TestSquad.Selection(), [15], [99], _players, budgetRemaining: 7.0m);
        Assert.Contains("Player 99 has no known position yet, so he can't be picked.", outcome.Check.Errors);
    }

    [Fact]
    public void Transfer_over_the_remaining_budget_fails()
    {
        // Sell attacker 31 (8.0), buy attacker 34 (9.0) with only 0.5 in the bank.
        var outcome = _rules.ApplyTransfers(TestSquad.Selection(), [31], [34], _players, budgetRemaining: 0.5m);
        Assert.Contains("These transfers cost R0.5m more than you have (R0.5m left).", outcome.Check.Errors);
    }

    [Fact]
    public void Transfer_that_breaks_the_club_limit_fails()
    {
        _players[16] = _players[16] with { ClubId = 6, ClubName = "Club 6" }; // club 6 already has 21 and 24
        _players[26] = _players[26] with { ClubId = 6, ClubName = "Club 6" };
        var outcome = _rules.ApplyTransfers(TestSquad.Selection(), [15, 25], [16, 26], _players, budgetRemaining: 10m);

        Assert.Contains("Max 3 players from Club 6; you have 4.", outcome.Check.Errors);
    }

    [Fact]
    public void Transferred_in_player_takes_the_outgoing_players_place_and_armband()
    {
        var current = TestSquad.Selection("4-4-2"); // captain 21 starts
        var outcome = _rules.ApplyTransfers(current, [21], [26], _players, budgetRemaining: 0m);

        Assert.True(outcome.Check.IsValid, string.Join(" | ", outcome.Check.Errors));
        Assert.Contains(26, outcome.NewSelection!.Starters);
        Assert.Equal(26, outcome.NewSelection.CaptainId);
    }

    [Fact]
    public void Cannot_buy_a_player_already_in_the_squad()
    {
        var outcome = _rules.ApplyTransfers(TestSquad.Selection(), [15], [14], _players, budgetRemaining: 5m);
        Assert.Contains("Player 14 is already in your squad.", outcome.Check.Errors);
    }

    [Fact]
    public void Cannot_sell_a_player_not_in_the_squad()
    {
        var outcome = _rules.ApplyTransfers(TestSquad.Selection(), [16], [3], _players, budgetRemaining: 5m);
        Assert.Contains("Player 16 isn't in your squad.", outcome.Check.Errors);
    }
}
