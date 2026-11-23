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
    public static GameweekInfo WritableGameweek(FantasyContext ctx, int? requestedNumber) => WritableGameweek(ctx, requestedNumber, null, requireTarget: false);

    /// <summary>
    /// The gameweek a write goes to. The request names it (gameweekId; the number is accepted for older
    /// clients). It must be the next gameweek, the first whose deadline hasn't passed. A request for a
    /// gameweek that has locked is refused with 409 gameweek_locked and the current state, so a stale
    /// request never edits the following week. With <paramref name="requireTarget"/>, naming no
    /// gameweek is 400 target_gameweek_required.
    /// </summary>
    public static GameweekInfo WritableGameweek(FantasyContext ctx, int? requestedNumber, int? requestedId, bool requireTarget, object? current = null)
    {
        if (requireTarget && requestedNumber is null && requestedId is null)
        {
            throw new RuleViolationException(
                ["Say which gameweek this change is for: send gameweekId (from GET /api/v1/fantasy/team/squad or /snapshot)."],
                "Target gameweek missing", ErrorCodes.TargetGameweekRequired);
        }
        var next = ctx.Calendar.Next(ctx.Now) ?? throw new ConflictException(
            "There's no upcoming gameweek to change. An admin needs to create the next gameweeks.", "No upcoming gameweek",
            code: ErrorCodes.GameweekLocked);

        if ((requestedId is null || requestedId == next.Id) && (requestedNumber is null || requestedNumber == next.Number)) return next;

        var requested = (requestedId is { } id ? ctx.Calendar.ById(id) : ctx.Calendar.ByNumber(requestedNumber!.Value))
            ?? throw new RuleViolationException($"Gameweek {requestedId?.ToString() ?? requestedNumber?.ToString()} doesn't exist.");
        if (requested.IsLocked(ctx.Now))
        {
            throw new ConflictException(
                $"Gameweek {requested.Number} locked at {requested.Deadline:yyyy-MM-dd HH:mm} UTC, so this change wasn't saved. " +
                $"Changes now apply to Gameweek {next.Number} (id {next.Id}).",
                "Gameweek locked", current ?? new { nextGameweek = next.ToRef(ctx.Now), serverTimeUtc = ctx.Now }, ErrorCodes.GameweekLocked);
        }
        throw new RuleViolationException($"Changes can only be made for the next gameweek (Gameweek {next.Number}, id {next.Id}).");
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
                    await currentState(),
                    ErrorCodes.StaleSquad);
            case IfMatchResult.Unreadable:
                throw new RuleViolationException("If-Match must be the ETag from GET /api/v1/fantasy/team/squad or /snapshot.");
        }
    }

    /// <summary>
    /// Serialises writes to one team: locks its app.fantasy_teams row (SELECT … FOR UPDATE) inside the
    /// caller's transaction, then reloads it. A second device's write waits here, then reads the version
    /// the first one committed and gets 409 stale_squad, instead of both updating the same squad rows.
    /// </summary>
    public static async Task LockTeamAsync(Data.TouchlineDbContext db, FantasyTeam team, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM app.fantasy_teams WHERE id = {team.Id} FOR UPDATE", ct);
        var entry = db.Entry(team);
        if (entry.State != Microsoft.EntityFrameworkCore.EntityState.Detached) await entry.ReloadAsync(ct);
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
