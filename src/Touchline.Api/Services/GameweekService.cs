using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>A gameweek with its number in the season, deadline and fixtures.</summary>
public sealed record GameweekInfo(Gameweek Gameweek, int Number, DateTimeOffset? Deadline, IReadOnlyList<Fixture> Fixtures)
{
    public int Id => Gameweek.Id;
    public bool IsFirst => Number == 1;
    public bool IsLocked(DateTimeOffset now) => GameweekRules.IsLocked(Deadline, now);

    public GameweekRef ToRef(DateTimeOffset now) =>
        new(Gameweek.Id, Number, Gameweek.Name, Gameweek.StartDate, Gameweek.EndDate, Deadline, IsLocked(now));
}

/// <summary>All gameweeks of a season in order. Deadlines come from deadline_at, or 90 minutes before the first kickoff.</summary>
public sealed record SeasonCalendar(int SeasonId, IReadOnlyList<GameweekInfo> Gameweeks)
{
    /// <summary>The first gameweek whose deadline hasn't passed: where squad changes go.</summary>
    public GameweekInfo? Next(DateTimeOffset now) => Gameweeks.FirstOrDefault(g => g.Deadline is not null && !g.IsLocked(now));

    /// <summary>The latest gameweek whose deadline has passed: the one being played or last played.</summary>
    public GameweekInfo? Current(DateTimeOffset now) => Gameweeks.LastOrDefault(g => g.IsLocked(now));

    public GameweekInfo? ByNumber(int number) => Gameweeks.FirstOrDefault(g => g.Number == number);

    public GameweekInfo? ById(int id) => Gameweeks.FirstOrDefault(g => g.Id == id);
}

public sealed class GameweekService(TouchlineDbContext db, IOptions<FantasyRules> rules)
{
    private readonly GameweekRules _rules = new(rules.Value);

    public async Task<IReadOnlyList<GameweekSummary>> GetGameweeksAsync(int? seasonId, CancellationToken ct = default)
    {
        var query = db.Gameweeks.AsQueryable();
        if (seasonId is not null)
        {
            query = query.Where(g => g.SeasonId == seasonId);
        }

        // Select only the columns that exist before V034 (deadline_at is new), so this endpoint
        // keeps working until the migration is applied.
        var gameweeks = await query
            .OrderBy(g => g.StartDate)
            .Select(g => new { g.Id, g.Name, g.StartDate, g.EndDate, g.IsActive, g.SeasonId })
            .ToListAsync(ct);
        var ids = gameweeks.Select(g => g.Id).ToList();

        var readyIds = await db.GameweekSyncStatuses
            .Where(s => ids.Contains(s.GameweekId) && s.MarkedComplete)
            .Select(s => s.GameweekId)
            .ToListAsync(ct);
        var ready = readyIds.ToHashSet();

        return gameweeks
            .Select(g => new GameweekSummary(
                g.Id, g.Name, g.StartDate, g.EndDate, g.IsActive, g.SeasonId, ready.Contains(g.Id)))
            .ToList();
    }

    /// <summary>The season's gameweeks with numbers, deadlines and fixtures. Needs V034 (deadline_at).</summary>
    public async Task<SeasonCalendar> GetCalendarAsync(int seasonId, CancellationToken ct = default)
    {
        var gameweeks = await db.Gameweeks
            .Where(g => g.SeasonId == seasonId)
            .OrderBy(g => g.StartDate)
            .ToListAsync(ct);

        var fixtures = await db.Fixtures
            .Where(f => f.SeasonId == seasonId && !f.IsPlaceholder && f.StartingAt != null)
            .OrderBy(f => f.StartingAt)
            .ToListAsync(ct);

        var infos = gameweeks.Select((g, i) =>
        {
            var inWeek = fixtures.Where(f => GameweekRules.FixtureBelongs(f.StartingAt!.Value, g.StartDate, g.EndDate)).ToList();
            var deadline = g.DeadlineAt ?? _rules.Deadline(inWeek.Select(f => f.StartingAt));
            return new GameweekInfo(g, i + 1, deadline, inWeek);
        }).ToList();

        return new SeasonCalendar(seasonId, infos);
    }

    /// <summary>Whether the sync worker has marked a gameweek complete (the scoring gate).</summary>
    public Task<bool> IsMarkedCompleteAsync(int gameweekId, CancellationToken ct = default) =>
        db.GameweekSyncStatuses.AnyAsync(s => s.GameweekId == gameweekId && s.MarkedComplete, ct);

    /// <summary>Admin: create a gameweek. The deadline defaults to 90 minutes before its first kickoff.</summary>
    public async Task<GameweekRef> CreateAsync(CreateGameweekRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 50)
        {
            errors.Add("Give the gameweek a name of 1 to 50 characters.");
        }
        if (request.EndDate < request.StartDate)
        {
            errors.Add("The end date can't be before the start date.");
        }
        if (!await db.Seasons.AnyAsync(s => s.Id == request.SeasonId, ct))
        {
            errors.Add($"Season {request.SeasonId} doesn't exist.");
        }

        var overlapping = await db.Gameweeks
            .Where(g => g.SeasonId == request.SeasonId && g.StartDate <= request.EndDate && g.EndDate >= request.StartDate)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(ct);
        if (overlapping is not null)
        {
            errors.Add($"These dates overlap {overlapping}.");
        }
        if (errors.Count > 0) throw new RuleViolationException(errors);

        var fixtures = await db.Fixtures
            .Where(f => f.SeasonId == request.SeasonId && !f.IsPlaceholder && f.StartingAt != null)
            .Select(f => f.StartingAt)
            .ToListAsync(ct);
        var inWeek = fixtures.Where(k => GameweekRules.FixtureBelongs(k!.Value, request.StartDate, request.EndDate));

        var gameweek = new Gameweek
        {
            SeasonId = request.SeasonId,
            Name = request.Name.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsActive = false,
            DeadlineAt = request.DeadlineAt?.ToUniversalTime() ?? _rules.Deadline(inWeek),
        };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync(ct);

        var calendar = await GetCalendarAsync(request.SeasonId, ct);
        return calendar.ById(gameweek.Id)!.ToRef(now);
    }
}
