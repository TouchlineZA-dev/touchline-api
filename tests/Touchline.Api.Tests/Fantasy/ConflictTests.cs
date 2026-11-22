using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Touchline.Api.Services;

namespace Touchline.Api.Tests.Fantasy;

/// <summary>The checks behind the 409 responses: stale If-Match and offline changes after the deadline.</summary>
public class ConflictTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);

    private static FantasyContext Context()
    {
        GameweekInfo Week(int number, DateTimeOffset deadline) =>
            new(new Gameweek { Id = 100 + number, Name = $"Gameweek {number}", SeasonId = 1 }, number, deadline, []);

        var calendar = new SeasonCalendar(1, [Week(1, Now.AddDays(-14)), Week(2, Now.AddHours(-1)), Week(3, Now.AddDays(6))]);
        return new FantasyContext(new AppUser { Id = 1 }, new SeasonSummary(1, "2026/2027", 1, null, null), null, calendar, Now);
    }

    [Fact]
    public async Task Stale_if_match_returns_conflict_with_the_current_state()
    {
        var current = new { version = 5 };
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            FantasyContextService.CheckIfMatchAsync(SquadVersion.ETag(4, "abc"), currentVersion: 5, () => Task.FromResult<object>(current)));

        Assert.Equal("Team changed", ex.Title);
        Assert.Same(current, ex.Current);
    }

    [Fact]
    public async Task Current_or_missing_if_match_passes()
    {
        await FantasyContextService.CheckIfMatchAsync(SquadVersion.ETag(5, "abc"), 5, () => throw new InvalidOperationException());
        await FantasyContextService.CheckIfMatchAsync(null, 5, () => throw new InvalidOperationException());
    }

    [Fact]
    public async Task Unreadable_if_match_is_a_bad_request()
    {
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            FantasyContextService.CheckIfMatchAsync("\"something-else\"", 5, () => Task.FromResult<object>(new())));
    }

    [Fact]
    public void Changes_go_to_the_next_gameweek()
    {
        Assert.Equal(3, FantasyContextService.WritableGameweek(Context(), requestedNumber: null).Number);
        Assert.Equal(3, FantasyContextService.WritableGameweek(Context(), requestedNumber: 3).Number);
    }

    [Fact]
    public void Offline_change_for_a_locked_gameweek_is_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<ConflictException>(() => FantasyContextService.WritableGameweek(Context(), requestedNumber: 2));

        Assert.Equal("Gameweek locked", ex.Title);
        Assert.Contains("Gameweek 2 locked", ex.Message);
        Assert.Contains("Changes now apply to Gameweek 3", ex.Message);
    }

    [Fact]
    public void Next_and_current_gameweek_from_deadlines()
    {
        var ctx = Context();
        Assert.Equal(3, ctx.Calendar.Next(Now)!.Number);
        Assert.Equal(2, ctx.Calendar.Current(Now)!.Number);
    }
}
