using Microsoft.EntityFrameworkCore;
using Npgsql;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>Who's asking, for which season, with which team, and the season's gameweeks.</summary>
public sealed record FantasyContext(AppUser User, SeasonSummary Season, FantasyTeam? Team, SeasonCalendar Calendar, DateTimeOffset Now)
{
    public FantasyTeam RequireTeam() => Team ?? throw new NotFoundException(
        "You don't have a fantasy team for this season yet. Create one with POST /api/v1/fantasy/team.");
}

public sealed class FantasyContextService(TouchlineDbContext db, FootballService football, GameweekService gameweeks, TimeProvider time)
{
    public async Task<FantasyContext> LoadAsync(AppUser user, bool trackTeam, CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct)
            ?? throw new NotFoundException("No current PSL season is flagged is_current in football.seasons.");

        var teams = trackTeam ? db.FantasyTeams.AsTracking() : db.FantasyTeams;
        var team = await teams.FirstOrDefaultAsync(t => t.UserId == user.Id && t.SeasonId == season.Id, ct);
        var calendar = await gameweeks.GetCalendarAsync(season.Id, ct);
        return new FantasyContext(user, season, team, calendar, time.GetUtcNow());
    }

    /// <summary>
    /// Changes always go to the next gameweek (the first whose deadline hasn't passed). If the app
    /// says which gameweek it was editing and that one has locked (an offline change sent too late),
    /// the change is rejected with a clear message rather than silently applied to the next one.
    /// </summary>
    public static GameweekInfo WritableGameweek(FantasyContext ctx, int? requestedNumber)
    {
        var next = ctx.Calendar.Next(ctx.Now) ?? throw new ConflictException(
            "There's no upcoming gameweek to change. An admin needs to create the next gameweeks.", "No upcoming gameweek");

        if (requestedNumber is null || requestedNumber == next.Number) return next;

        var requested = ctx.Calendar.ByNumber(requestedNumber.Value)
            ?? throw new RuleViolationException($"Gameweek {requestedNumber} doesn't exist.");
        if (requested.IsLocked(ctx.Now))
        {
            throw new ConflictException(
                $"Gameweek {requested.Number} locked at {requested.Deadline:yyyy-MM-dd HH:mm} UTC, so this change wasn't saved. " +
                $"Changes now apply to Gameweek {next.Number}.",
                "Gameweek locked");
        }
        throw new RuleViolationException($"Changes can only be made for the next gameweek (Gameweek {next.Number}).");
    }

    /// <summary>The gameweek to show when none is asked for: the next one, or the current one at the end of the season.</summary>
    public static GameweekInfo DefaultGameweek(FantasyContext ctx, int? number)
    {
        if (number is not null)
        {
            return ctx.Calendar.ByNumber(number.Value) ?? throw new NotFoundException($"Gameweek {number} doesn't exist.");
        }
        return ctx.Calendar.Next(ctx.Now) ?? ctx.Calendar.Current(ctx.Now)
            ?? throw new NotFoundException("No gameweeks have been created for this season yet.");
    }

    /// <summary>If-Match: a stale version gets 409 with the current state, so an offline change can't overwrite a newer one.</summary>
    public static async Task CheckIfMatchAsync(string? ifMatch, int currentVersion, Func<Task<object>> currentState)
    {
        switch (SquadVersion.Check(ifMatch, currentVersion))
        {
            case IfMatchResult.Stale:
                throw new ConflictException(
                    "Your team changed since the app last loaded it, so nothing was saved. 'current' has the latest team.",
                    "Team changed",
                    await currentState());
            case IfMatchResult.Unreadable:
                throw new RuleViolationException("If-Match must be the ETag from GET /api/v1/fantasy/team/squad or /snapshot.");
        }
    }

    public static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        }
        return false;
    }
}
