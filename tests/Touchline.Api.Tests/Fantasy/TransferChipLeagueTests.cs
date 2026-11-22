using Touchline.Api.Business;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Tests.Fantasy;

public class TransferRulesTests
{
    private readonly TransferRules _transfers = new(new FantasyRules());

    [Fact]
    public void One_free_transfer_per_gameweek()
    {
        var cost = _transfers.Calculate(alreadyMade: 0, newTransfers: 1, unlimited: false);
        Assert.Equal(0, cost.GameweekCost);
        Assert.Equal(0, cost.FreeTransfersLeft);
    }

    [Fact]
    public void Each_extra_transfer_costs_four_points()
    {
        var cost = _transfers.Calculate(0, 3, unlimited: false);
        Assert.Equal(8, cost.GameweekCost);
        Assert.Equal(8, cost.RequestCost);
    }

    [Fact]
    public void Second_request_in_the_same_gameweek_pays_only_its_own_hit()
    {
        var cost = _transfers.Calculate(alreadyMade: 1, newTransfers: 1, unlimited: false);
        Assert.Equal(4, cost.GameweekCost);
        Assert.Equal(4, cost.RequestCost);
        Assert.Equal(2, cost.TransfersMade);
    }

    [Fact]
    public void Unused_free_transfers_do_not_roll_over()
    {
        // Last gameweek made 0 transfers; this gameweek still starts at 1 free, so 2 transfers cost 4.
        var cost = _transfers.Calculate(alreadyMade: 0, newTransfers: 2, unlimited: false);
        Assert.Equal(4, cost.GameweekCost);
    }

    [Fact]
    public void Wildcard_or_before_gameweek_one_is_unlimited_and_free()
    {
        var cost = _transfers.Calculate(alreadyMade: 3, newTransfers: 5, unlimited: true);
        Assert.Equal(0, cost.GameweekCost);
        Assert.Null(cost.FreeTransfersLeft);
        Assert.Equal(8, cost.TransfersMade);
    }

    [Fact]
    public void Hit_size_is_a_setting()
    {
        var custom = new TransferRules(new FantasyRules { TransferHitPoints = 6 });
        Assert.Equal(6, custom.Calculate(0, 2, false).GameweekCost);
    }
}

public class ChipRulesTests
{
    [Fact]
    public void A_chip_can_be_activated_once()
    {
        Assert.True(ChipRules.CanActivate(Chips.BenchBoost, [], gameweekId: 5).IsValid);
    }

    [Fact]
    public void Second_chip_in_the_same_gameweek_is_rejected()
    {
        var check = ChipRules.CanActivate(Chips.TripleCaptain, [new ChipUse(Chips.BenchBoost, 5)], gameweekId: 5);
        Assert.Contains("Bench Boost is already active this gameweek. Only one chip per gameweek.", check.Errors);
    }

    [Fact]
    public void Same_chip_twice_in_a_season_is_rejected()
    {
        var check = ChipRules.CanActivate(Chips.Wildcard, [new ChipUse(Chips.Wildcard, 2)], gameweekId: 9);
        Assert.Contains("You've already used Wildcard this season. Each chip can be used once per season.", check.Errors);
    }

    [Fact]
    public void Unknown_chip_is_rejected()
    {
        Assert.False(ChipRules.CanActivate("free_hit", [], 1).IsValid);
    }
}

public class LeagueRulesTests
{
    private static readonly FantasyRules Rules = new();
    private readonly LeagueRules _leagues = new(Rules);

    [Fact]
    public void Join_codes_have_the_right_format()
    {
        var random = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var code = _leagues.GenerateJoinCode(random);
            var body = code[3..];

            Assert.StartsWith("TL-", code);
            Assert.Equal(6, body.Count(char.IsAsciiDigit));
            Assert.Equal(2, body.Count(c => !char.IsAsciiDigit(c)));
            Assert.All(body.Where(c => !char.IsAsciiDigit(c)), c => Assert.Contains(c, "#!@-"));
            Assert.True(char.IsAsciiDigit(body[0]) && char.IsAsciiDigit(body[^1]), code);
            Assert.DoesNotContain(body.Zip(body.Skip(1)), pair => !char.IsAsciiDigit(pair.First) && !char.IsAsciiDigit(pair.Second));
            Assert.True(_leagues.IsValidJoinCode(code), code);
        }
    }

    [Theory]
    [InlineData("TL-482#91!3", true)]
    [InlineData("TL-48#29!1", false)]  // only 5 digits
    [InlineData("TL-4829#1!3", true)]
    [InlineData("TL-48##291", false)]  // specials next to each other
    [InlineData("TL-#48291!3", false)] // starts with a special
    [InlineData("TL-482913#!", false)]
    [InlineData("TL-482$91!3", false)] // $ isn't allowed
    [InlineData("tl-482#91!3", false)] // prefix is exact
    [InlineData("TL-482913", false)]   // no specials
    public void Join_code_validation(string code, bool valid)
    {
        Assert.Equal(valid, _leagues.IsValidJoinCode(code));
    }

    [Fact]
    public async Task Taken_join_codes_are_retried()
    {
        var taken = new HashSet<string>();
        var random = new Random(7);
        var first = _leagues.GenerateJoinCode(new Random(7));
        taken.Add(first);

        var calls = 0;
        var code = await _leagues.GenerateUniqueJoinCodeAsync(c => { calls++; return Task.FromResult(taken.Contains(c)); }, random);

        Assert.NotEqual(first, code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_attempts()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _leagues.GenerateUniqueJoinCodeAsync(_ => Task.FromResult(true), new Random(1)));
    }

    [Fact]
    public void League_cap_is_twenty_including_the_owner()
    {
        Assert.True(_leagues.CanJoin(activeMembers: 19, maxMembers: 20, alreadyMember: false).IsValid);
        Assert.Contains("This league is full (20 teams).", _leagues.CanJoin(20, 20, false).Errors);
    }

    [Fact]
    public void Cannot_join_twice()
    {
        Assert.Contains("Your team is already in this league.", _leagues.CanJoin(5, 20, alreadyMember: true).Errors);
    }

    [Fact]
    public void Owner_cannot_leave()
    {
        Assert.Contains("You own this league, so you can't leave it.", LeagueRules.CanLeave(isOwner: true).Errors);
        Assert.True(LeagueRules.CanLeave(isOwner: false).IsValid);
    }

    [Fact]
    public void Standings_tie_break_is_overall_points_then_username()
    {
        var ranked = LeagueRules.Rank(
        [
            new StandingInput(1, "A", "zola", Points: 50, OverallPoints: 300, false),
            new StandingInput(2, "B", "bongi", Points: 60, OverallPoints: 200, false),
            new StandingInput(3, "C", "amahle", Points: 50, OverallPoints: 300, false),
            new StandingInput(4, "D", "thabo", Points: 50, OverallPoints: 310, false),
        ]);

        Assert.Equal([2, 4, 3, 1], ranked.Select(r => r.Entry.FantasyTeamId));
        Assert.Equal([1, 2, 3, 4], ranked.Select(r => r.Rank));
    }
}

public class GameweekRulesTests
{
    private readonly GameweekRules _gameweeks = new(new FantasyRules());

    [Fact]
    public void Fixture_belongs_by_sast_date()
    {
        // 23:30 UTC on 28 Nov is 01:30 SAST on 29 Nov.
        var kickoff = new DateTimeOffset(2026, 11, 28, 23, 30, 0, TimeSpan.Zero);
        Assert.False(GameweekRules.FixtureBelongs(kickoff, new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 28)));
        Assert.True(GameweekRules.FixtureBelongs(kickoff, new DateOnly(2026, 11, 29), new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void Deadline_is_ninety_minutes_before_the_first_kickoff()
    {
        var first = new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero);
        var deadline = _gameweeks.Deadline([first.AddHours(5), first, null]);
        Assert.Equal(first.AddMinutes(-90), deadline);
    }

    [Fact]
    public void Locked_at_and_after_the_deadline()
    {
        var deadline = new DateTimeOffset(2026, 11, 28, 11, 30, 0, TimeSpan.Zero);
        Assert.False(GameweekRules.IsLocked(deadline, deadline.AddSeconds(-1)));
        Assert.True(GameweekRules.IsLocked(deadline, deadline));
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(50, 45)]   // half-time
    [InlineData(75, 60)]   // 75 on the clock minus 15 half-time
    [InlineData(130, 90)]  // capped at the length
    public void Live_minute_from_the_clock(int minutesSinceKickoff, int expected)
    {
        var kickoff = new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, _gameweeks.LiveMinute(kickoff, kickoff.AddMinutes(minutesSinceKickoff), length: null, latestEventMinute: null));
    }

    [Fact]
    public void Live_minute_never_lags_the_latest_event()
    {
        var kickoff = new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(52, _gameweeks.LiveMinute(kickoff, kickoff.AddMinutes(60), null, latestEventMinute: 52));
    }

    [Fact]
    public void Match_end_minute_by_status()
    {
        var kickoff = new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero);
        var now = kickoff.AddMinutes(40);
        Assert.Equal(90, _gameweeks.MatchEndMinute(MatchStatus.Finished, kickoff, now, null, null));
        Assert.Equal(40, _gameweeks.MatchEndMinute(MatchStatus.Live, kickoff, now, null, null));
        Assert.Equal(0, _gameweeks.MatchEndMinute(MatchStatus.Scheduled, kickoff, now, null, null));
    }

    [Fact]
    public void Finalise_only_when_marked_complete_or_every_fixture_is_final()
    {
        Assert.True(GameweekRules.CanFinalise(false, [5, 7, 8]));
        Assert.False(GameweekRules.CanFinalise(false, [5, 22]));
        Assert.True(GameweekRules.CanFinalise(true, [5, 22]));
        Assert.False(GameweekRules.CanFinalise(false, []));
    }

    [Fact]
    public void Live_fixture_gives_provisional_points_and_finished_gives_final()
    {
        var scoring = new PlayerScoring(new FantasyRules());
        var kickoff = new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero);
        var now = kickoff.AddMinutes(80); // minute 65

        var live = new PlayerFixtureFacts
        {
            Started = true,
            MatchEndMinute = _gameweeks.MatchEndMinute(MatchStatus.Live, kickoff, now, null, null),
            FixtureFinished = false,
            Goals = 1,
            GoalsConceded = 0,
        };
        var provisional = scoring.ScoreFixture(live, Position.DEF);

        Assert.True(provisional.Provisional);
        Assert.Equal(65, provisional.Minutes);
        Assert.Equal(1 + 3 + 2, provisional.Total); // 45+ appearance, goal, provisional clean sheet

        var final = scoring.ScoreFixture(live with { MatchEndMinute = 90, FixtureFinished = true }, Position.DEF);
        Assert.False(final.Provisional);
        Assert.Equal(2 + 3 + 2, final.Total);
    }
}

public class SquadVersionTests
{
    [Fact]
    public void Stale_version_is_detected()
    {
        Assert.Equal(IfMatchResult.Stale, SquadVersion.Check("\"v3-abc\"", currentVersion: 4));
    }

    [Fact]
    public void Current_version_passes()
    {
        Assert.Equal(IfMatchResult.Current, SquadVersion.Check(SquadVersion.ETag(4, "abc"), 4));
        Assert.Equal(IfMatchResult.Current, SquadVersion.Check("W/\"v4-abc\"", 4));
    }

    [Fact]
    public void Missing_header_skips_the_check_and_junk_is_unreadable()
    {
        Assert.Equal(IfMatchResult.NotSent, SquadVersion.Check(null, 4));
        Assert.Equal(IfMatchResult.NotSent, SquadVersion.Check("*", 4));
        Assert.Equal(IfMatchResult.Unreadable, SquadVersion.Check("\"h-1234\"", 4));
    }
}
