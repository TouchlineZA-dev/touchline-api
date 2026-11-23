using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Touchline.Api.Services;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;

namespace Touchline.Api.Tests.Reference;

/// <summary>Sprint 2 rule tests under OUR rules (runbook Part 3), next to Phatu's FPL-rule figures.</summary>
public sealed class SeasonRuleTests
{
    private static readonly FantasyRules Rules = new();

    private static PointsBreakdown Pts(int total, int minutes) => new() { Total = total, Minutes = minutes, Fixtures = 1 };

    /// <summary>Nine other starters scoring 30 in total, plus captain A, vice B and bench player C.</summary>
    private static List<TeamPickPoints> Team(PointsBreakdown captain, PointsBreakdown vice)
    {
        var picks = new List<TeamPickPoints>
        {
            new(1, true, true, false, captain),
            new(2, true, false, true, vice),
        };
        int[] others = [4, 4, 4, 3, 3, 3, 3, 3, 3];   // 30
        picks.AddRange(others.Select((p, i) => new TeamPickPoints(10 + i, true, false, false, Pts(p, 90))));
        picks.Add(new TeamPickPoints(30, false, false, false, Pts(4, 90)));   // bench C: 4 points, no autosub
        return picks;
    }

    [Fact]
    public void Captain_plays_zero_vice_scores_six_nine_others_thirty_minus_four_is_44()
    {
        var score = new TeamScoring(Rules).Score(Team(captain: Pts(0, 0), vice: Pts(6, 90)), chip: null, transferCost: 4);
        // 30 + 6 × 3 (vice takes the armband) + 0 (no autosub for C) − 4 = 44.
        Assert.Equal(44, score.Total);
        Assert.Equal(3, score.Picks.Single(p => p.PlayerId == 2).Multiplier);
        Assert.False(score.Picks.Single(p => p.PlayerId == 30).Counted);
    }

    [Fact]
    public void Captain_plays_only_the_second_fixture_of_a_double_week_and_keeps_the_armband_41()
    {
        // Captain: 0 minutes in fixture 1, plays fixture 2 and scores 1 point there; minutes summed across both.
        var captain = Pts(0, 0).Add(Pts(1, 20));
        var score = new TeamScoring(Rules).Score(Team(captain, vice: Pts(6, 90)), chip: null, transferCost: 4);
        // 30 + 1 × 3 + 6 × 2 − 4 = 41.
        Assert.Equal(41, score.Total);
        Assert.Equal(3, score.Picks.Single(p => p.PlayerId == 1).Multiplier);
        Assert.Equal(2, score.Picks.Single(p => p.PlayerId == 2).Multiplier);
    }

    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(true, null, false, true)]                 // before the GW1 deadline
    [InlineData(false, Chips.Wildcard, false, true)]
    [InlineData(false, null, true, true)]                 // late entrant before its first deadline
    public void Late_entrants_get_unlimited_free_transfers_before_their_first_deadline(bool gw1, string? chip, bool teamsFirstWeek, bool unlimited) =>
        Assert.Equal(unlimited, TransferRules.UnlimitedFreeTransfers(gw1, chip, teamsFirstWeek));

    [Fact]
    public void Deadline_is_published_in_utc_and_sast()
    {
        var gw = new GameweekRef(1, 10, "Gameweek 10", new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 29),
            new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero), false);
        Assert.Equal("2026-11-24T18:00:00+02:00", gw.DeadlineSast);
    }
}

/// <summary>Versioned scoring and deadline protection against canonical Postgres.</summary>
public sealed class SeasonRuleDbTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    [SkippableFact]
    public async Task A_passed_deadline_never_changes_and_a_future_one_can_move_before_the_first_kickoff()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var locked = await f.GameweekAsync("Deadline GW past", new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 10), f.Clock.Now.AddHours(-1));
        var future = await f.GameweekAsync("Deadline GW future", new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 7), f.Clock.Now.AddDays(5));
        await f.FixtureAsync(f.Clock.Now.AddDays(5).AddMinutes(90), 1);   // its first kickoff, in its dates? not needed: date window check below
        using var scope = f.Api.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GameweekService>();

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateDeadlineAsync(locked, new UpdateDeadlineRequest(f.Clock.Now.AddDays(1), "Try to reopen"), f.Clock.Now, default));
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            service.UpdateDeadlineAsync(future, new UpdateDeadlineRequest(f.Clock.Now.AddMinutes(-5), "In the past"), f.Clock.Now, default));
        var moved = await service.UpdateDeadlineAsync(future, new UpdateDeadlineRequest(f.Clock.Now.AddDays(4), "First kickoff moved"), f.Clock.Now, default);
        Assert.Equal(f.Clock.Now.AddDays(4), moved.Deadline);
    }

    [SkippableFact]
    public async Task Finalising_stamps_the_rules_version_and_a_new_version_never_rescores_the_past()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var gw = await f.GameweekAsync("Versions GW", new DateOnly(2027, 4, 5), new DateOnly(2027, 4, 11), new DateTimeOffset(2027, 4, 5, 10, 0, 0, TimeSpan.Zero));
        var fixture = await f.FixtureAsync(new DateTimeOffset(2027, 4, 6, 15, 0, 0, TimeSpan.Zero), 5, 1, 0);
        var user = await f.Db.ScalarAsync<int>("INSERT INTO app.users (email, username) VALUES ($1, $1) RETURNING id", $"v-{Guid.NewGuid():N}");
        var team = await f.Db.ScalarAsync<int>("INSERT INTO app.fantasy_teams (user_id, season_id, name) VALUES ($1, $2, 'Versioned XI') RETURNING id", user, f.SeasonId);
        // A squad of 15 (positions don't matter for scoring); one striker scores the only goal.
        var players = new List<(int Id, long Sm)>();
        for (var i = 0; i < 15; i++) players.Add(await f.PlayerAsync($"V{i}", (short)(i < 2 ? 24 : i < 7 ? 25 : i < 12 ? 26 : 27)));
        foreach (var (p, i) in players.Select((p, i) => (p, i)))
        {
            await f.LineupAsync(fixture, f.Home, p.Sm, 11);
            await f.Db.ExecAsync("""
                INSERT INTO app.fantasy_squads (fantasy_team_id, player_id, gameweek_id, is_captain, is_vice_captain, is_on_bench, bench_order)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """, team, p.Id, gw, i == 14, i == 13, i is 1 or 6 or 11 or 12, i switch { 1 => (short?)1, 6 => 2, 11 => 3, 12 => 4, _ => null });
        }
        await f.Db.ExecAsync("INSERT INTO app.fantasy_gameweek_entries (fantasy_team_id, gameweek_id, formation) VALUES ($1, $2, '4-4-2')", team, gw);
        await f.EventAsync(fixture, FootballDbFixture.NextId(), 14, f.Home, players[14].Sm, null, 10);

        using (var scope = f.Api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PointsService>().FinaliseAsync(gw, f.Clock.Now, default);
        }
        Assert.Equal(Rulebook.Version, await f.Db.ScalarAsync<string>("SELECT rules_version FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, gw));
        Assert.Equal(Rulebook.PointsTableVersion, await f.Db.ScalarAsync<string>("SELECT DISTINCT points_table_version FROM app.points_results WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, gw));
        var points = await f.Db.ScalarAsync<int>("SELECT points FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, gw);

        // Pretend the entry was scored under an older rulebook, and change the facts: a rerun must not touch it.
        await f.Db.ExecAsync("UPDATE app.fantasy_gameweek_entries SET rules_version = '2025-26.v1' WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, gw);
        await f.EventAsync(fixture, FootballDbFixture.NextId(), 14, f.Home, players[2].Sm, null, 20);
        using (var scope = f.Api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PointsService>().FinaliseAsync(gw, f.Clock.Now, default);
        }
        Assert.Equal(points, await f.Db.ScalarAsync<int>("SELECT points FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, gw));
        Assert.Equal("2025-26.v1", await f.Db.ScalarAsync<string>("SELECT rules_version FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, gw));
    }
}
