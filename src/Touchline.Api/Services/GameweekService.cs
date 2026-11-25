using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>Where a fixture's gameweek came from.</summary>
public enum AssignmentSource
{
    /// <summary>A row in app.fixture_gameweek_assignments (migration 007 / V043).</summary>
    Persisted,
    /// <summary>Legacy: the gameweek whose SAST dates contain the kickoff (used until 007 is applied, or for a fixture with no row yet).</summary>
    DateWindow,
}

/// <summary>A gameweek with its public number, deadline and fixtures.</summary>
public sealed record GameweekInfo(Gameweek Gameweek, int Number, DateTimeOffset? Deadline, IReadOnlyList<Fixture> Fixtures)
{
    public int Id => Gameweek.Id;
    public bool IsFirst => Number == 1;
    public bool IsLocked(DateTimeOffset now) => GameweekRules.IsLocked(Deadline, now);

    public GameweekRef ToRef(DateTimeOffset now) =>
        new(Gameweek.Id, Number, Gameweek.Name, Gameweek.StartDate, Gameweek.EndDate, Deadline, IsLocked(now));
}

/// <summary>All gameweeks of a season in order. Deadlines come from deadline_at, or 90 minutes before the first kickoff.</summary>
/// <param name="NumbersPersisted">Gameweek numbers come from app.gameweeks.number (007) rather than row order.</param>
/// <param name="AssignmentSources">How each fixture in the calendar got its gameweek, by fixture id.</param>
/// <param name="Unassigned">Season fixtures in no gameweek (e.g. postponed with no new date).</param>
public sealed record SeasonCalendar(
    int SeasonId,
    IReadOnlyList<GameweekInfo> Gameweeks,
    bool NumbersPersisted = false,
    IReadOnlyDictionary<int, AssignmentSource>? AssignmentSources = null,
    IReadOnlyList<Fixture>? Unassigned = null)
{
    /// <summary>The first gameweek whose deadline hasn't passed: where squad changes go.</summary>
    public GameweekInfo? Next(DateTimeOffset now) => Gameweeks.FirstOrDefault(g => g.Deadline is not null && !g.IsLocked(now));

    /// <summary>The latest gameweek whose deadline has passed: the one being played or last played.</summary>
    public GameweekInfo? Current(DateTimeOffset now) => Gameweeks.LastOrDefault(g => g.IsLocked(now));

    public GameweekInfo? ByNumber(int number) => Gameweeks.FirstOrDefault(g => g.Number == number);

    public GameweekInfo? ById(int id) => Gameweeks.FirstOrDefault(g => g.Id == id);

    public GameweekInfo? ForFixture(int fixtureId) => Gameweeks.FirstOrDefault(g => g.Fixtures.Any(f => f.Id == fixtureId));
}

/// <summary>One row of a fixture's assignment history.</summary>
public sealed record FixtureAssignmentView(
    int Id, int FixtureId, int? GameweekId, int? GameweekNumber, string Reason, int? AssignedByUserId, DateTimeOffset CreatedAtUtc);

public sealed record UpdateDeadlineRequest(DateTimeOffset DeadlineAt, string Reason);

public sealed record AssignFixtureRequest(int? GameweekId, string Reason, bool ConfirmLockedChange = false);

public sealed record AssignFixtureResult(
    int FixtureId, int? FromGameweekId, int? ToGameweekId, bool AffectsLockedGameweek, bool RescoreNeeded,
    IReadOnlyList<FixtureAssignmentView> History, DateTimeOffset ServerTimeUtc);

internal sealed class GameweekNumberRow
{
    public int Id { get; set; }
    public short? Number { get; set; }
}

internal sealed class AssignmentRow
{
    public int Id { get; set; }
    public int FixtureId { get; set; }
    public int? GameweekId { get; set; }
    public string Reason { get; set; } = "";
    public int? AssignedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GameweekService(TouchlineDbContext db, IOptions<FantasyRules> rules, SchemaFeatures schema)
{
    private readonly GameweekRules _rules = new(rules.Value);

    public async Task<IReadOnlyList<GameweekSummary>> GetGameweeksAsync(int? seasonId, CancellationToken ct = default)
    {
        var query = db.Gameweeks.AsQueryable();
        if (seasonId is not null)
        {
            query = query.Where(g => g.SeasonId == seasonId);
        }

        // Select only the columns that exist before docs/migrations/003 (deadline_at is new), so this endpoint
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
        var numbers = await PersistedNumbersAsync(ids, ct);

        // Deadlines as the fantasy routes use them (stored, or 90 minutes before the first kickoff), so the
        // app can show "pick your team by …" before sign-in. Absent on a database older than migration 003.
        var deadlines = new Dictionary<int, DateTimeOffset?>();
        foreach (var season in gameweeks.Select(g => g.SeasonId).OfType<int>().Distinct())
        {
            try
            {
                foreach (var info in (await GetCalendarAsync(season, ct)).Gameweeks) deadlines[info.Gameweek.Id] = info.Deadline;
            }
            catch (Exception ex) when (ex.GetBaseException() is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UndefinedColumn })
            {
            }
        }

        return gameweeks
            .Select(g => new GameweekSummary(
                g.Id, g.Name, g.StartDate, g.EndDate, g.IsActive, g.SeasonId, ready.Contains(g.Id),
                numbers.TryGetValue(g.Id, out var n) ? n : null,
                deadlines.GetValueOrDefault(g.Id)))
            .ToList();
    }

    /// <summary>
    /// The season's gameweeks with numbers, deadlines and fixtures. Uses the persisted numbers and
    /// fixture assignments from migration 007 when it is applied; until then (and for a fixture with no
    /// assignment row yet) falls back to row order and SAST date windows, reporting which was used.
    /// </summary>
    public async Task<SeasonCalendar> GetCalendarAsync(int seasonId, CancellationToken ct = default)
    {
        var gameweeks = await db.Gameweeks
            .Where(g => g.SeasonId == seasonId)
            .OrderBy(g => g.StartDate).ThenBy(g => g.Id)
            .ToListAsync(ct);

        var fixtures = await db.Fixtures
            .Where(f => f.SeasonId == seasonId && !f.IsPlaceholder && f.StartingAt != null)
            .OrderBy(f => f.StartingAt).ThenBy(f => f.Id)
            .ToListAsync(ct);

        var numbers = await PersistedNumbersAsync(gameweeks.Select(g => g.Id).ToList(), ct);
        var numbersPersisted = gameweeks.Count > 0 && gameweeks.All(g => numbers.ContainsKey(g.Id));
        var current = await CurrentAssignmentsAsync(fixtures.Select(f => f.Id).ToList(), ct);

        var sources = new Dictionary<int, AssignmentSource>();
        var byGameweek = gameweeks.ToDictionary(g => g.Id, _ => new List<Fixture>());
        var unassigned = new List<Fixture>();
        foreach (var fixture in fixtures)
        {
            int? gameweekId;
            if (current.TryGetValue(fixture.Id, out var row))
            {
                gameweekId = row.GameweekId;
                sources[fixture.Id] = AssignmentSource.Persisted;
            }
            else
            {
                gameweekId = gameweeks.FirstOrDefault(g => GameweekRules.FixtureBelongs(fixture.StartingAt!.Value, g.StartDate, g.EndDate))?.Id;
                sources[fixture.Id] = AssignmentSource.DateWindow;
            }

            if (gameweekId is { } id && byGameweek.TryGetValue(id, out var list)) list.Add(fixture);
            else unassigned.Add(fixture);
        }

        var infos = gameweeks.Select((g, i) =>
        {
            var inWeek = byGameweek[g.Id];
            var deadline = g.DeadlineAt ?? _rules.Deadline(inWeek.Select(f => f.StartingAt));
            var number = numbersPersisted ? numbers[g.Id] : i + 1;
            return new GameweekInfo(g, number, deadline, inWeek);
        }).OrderBy(g => g.Number).ToList();

        return new SeasonCalendar(seasonId, infos, numbersPersisted, sources, unassigned);
    }

    /// <summary>Whether the sync worker has marked a gameweek complete (the scoring gate).</summary>
    public Task<bool> IsMarkedCompleteAsync(int gameweekId, CancellationToken ct = default) =>
        db.GameweekSyncStatuses.AnyAsync(s => s.GameweekId == gameweekId && s.MarkedComplete, ct);

    /// <summary>
    /// Admin: create a gameweek. The deadline defaults to 90 minutes before its first kickoff. With 007
    /// applied it gets the next public number (never renumbering existing weeks), and the season's
    /// fixtures in its dates that have no gameweek yet are assigned to it.
    /// </summary>
    public async Task<GameweekRef> CreateAsync(CreateGameweekRequest request, DateTimeOffset now, int? adminUserId = null, CancellationToken ct = default)
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
            .Select(f => new { f.Id, f.StartingAt, f.StateId })
            .ToListAsync(ct);
        var inWeek = fixtures.Where(f => GameweekRules.FixtureBelongs(f.StartingAt!.Value, request.StartDate, request.EndDate)).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var gameweek = new Gameweek
        {
            SeasonId = request.SeasonId,
            Name = request.Name.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            IsActive = false,
            DeadlineAt = request.DeadlineAt?.ToUniversalTime() ?? _rules.Deadline(inWeek.Select(f => f.StartingAt)),
        };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync(ct);

        if (await schema.HasColumnAsync("app", "gameweeks", "number", ct))
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE app.gameweeks
                SET number = (SELECT COALESCE(MAX(number), 0) + 1 FROM app.gameweeks WHERE season_id = {request.SeasonId})
                WHERE id = {gameweek.Id}
                """, ct);
        }
        if (await schema.HasTableAsync("app", "fixture_gameweek_assignments", ct))
        {
            var current = await CurrentAssignmentsAsync(inWeek.Select(f => f.Id).ToList(), ct);
            foreach (var fixture in inWeek.Where(f => !current.TryGetValue(f.Id, out var row) || row.GameweekId is null))
            {
                await InsertAssignmentAsync(fixture.Id, gameweek.Id, $"Assigned when {gameweek.Name} was created", adminUserId, ct);
            }
        }
        await transaction.CommitAsync(ct);

        var calendar = await GetCalendarAsync(request.SeasonId, ct);
        return calendar.ById(gameweek.Id)!.ToRef(now);
    }

    /// <summary>
    /// Admin: move a fixture to another gameweek (or unassign it with gameweekId null). Always recorded
    /// with a reason and the admin. A change that touches a gameweek whose deadline has passed rewrites
    /// what locked entries score, so it needs ConfirmLockedChange = true (a recorded decision); if that
    /// gameweek was already finalised, the result says it needs a rescore.
    /// </summary>
    public async Task<AssignFixtureResult> AssignFixtureAsync(int fixtureId, AssignFixtureRequest request, int adminUserId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("app", "fixture_gameweek_assignments", ct))
        {
            throw new SchemaNotReadyException("Fixture assignments need docs/migrations/007_gameweek_calendar.sql (touchline-sync V043).");
        }
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RuleViolationException("Every fixture move needs a reason.");

        var fixture = await db.Fixtures.FirstOrDefaultAsync(f => f.Id == fixtureId, ct)
            ?? throw new NotFoundException($"Fixture {fixtureId} doesn't exist.");
        var calendar = await GetCalendarAsync(fixture.SeasonId, ct);
        var from = calendar.ForFixture(fixtureId);
        GameweekInfo? to = null;
        if (request.GameweekId is { } targetId)
        {
            to = calendar.ById(targetId) ?? throw new RuleViolationException($"Gameweek {targetId} isn't in this fixture's season.");
        }
        if (from?.Id == to?.Id && calendar.AssignmentSources?.GetValueOrDefault(fixtureId) == AssignmentSource.Persisted)
        {
            throw new RuleViolationException("The fixture is already in that gameweek.");
        }

        var locked = (from?.IsLocked(now) ?? false) || (to?.IsLocked(now) ?? false);
        if (locked && !request.ConfirmLockedChange)
        {
            throw new ConflictException(
                "This move changes a gameweek whose deadline has passed, so locked teams would score differently. " +
                "Send confirmLockedChange: true with a reason to record the decision.",
                "Gameweek locked");
        }

        var touched = new[] { from?.Id, to?.Id }.Where(id => id is not null).Select(id => id!.Value).ToList();
        var rescoreNeeded = await db.GameweekEntries.AnyAsync(e => touched.Contains(e.GameweekId) && e.ScoredAt != null, ct);

        var reason = request.Reason.Trim();
        if (locked) reason = "[locked gameweek change confirmed] " + reason;
        await InsertAssignmentAsync(fixtureId, to?.Id, reason, adminUserId, ct);

        return new AssignFixtureResult(fixtureId, from?.Id, to?.Id, locked, rescoreNeeded, await HistoryAsync(fixtureId, ct), now);
    }

    /// <summary>
    /// Admin: change a FUTURE gameweek's deadline (e.g. its first kickoff moved). A deadline that has
    /// passed never changes, so published history and locked entries are protected. The new deadline
    /// must still be in the future and no later than the gameweek's first kickoff.
    /// </summary>
    public async Task<GameweekRef> UpdateDeadlineAsync(int gameweekId, UpdateDeadlineRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RuleViolationException("Every deadline change needs a reason.");
        var gameweek = await db.Gameweeks.AsTracking().FirstOrDefaultAsync(g => g.Id == gameweekId, ct)
            ?? throw new NotFoundException($"Gameweek {gameweekId} doesn't exist.");
        if (gameweek.SeasonId is not { } seasonId) throw new RuleViolationException("This gameweek has no season.");

        var info = (await GetCalendarAsync(seasonId, ct)).ById(gameweekId)!;
        if (info.IsLocked(now))
        {
            throw new ConflictException(
                $"{gameweek.Name}'s deadline ({info.Deadline:yyyy-MM-dd HH:mm} UTC) has passed, so it can't change.", "Gameweek locked");
        }
        var deadline = request.DeadlineAt.ToUniversalTime();
        var firstKickoff = info.Fixtures.Select(f => f.StartingAt).Where(k => k is not null).Min();
        var errors = new List<string>();
        if (deadline <= now) errors.Add("The new deadline must be in the future.");
        if (firstKickoff is not null && deadline > firstKickoff) errors.Add($"The deadline can't be after the first kickoff ({firstKickoff:yyyy-MM-dd HH:mm} UTC).");
        if (errors.Count > 0) throw new RuleViolationException(errors);

        gameweek.DeadlineAt = deadline;
        await db.SaveChangesAsync(ct);
        return (await GetCalendarAsync(seasonId, ct)).ById(gameweekId)!.ToRef(now);
    }

    /// <summary>A fixture's assignment history, newest first. Empty until 007 is applied.</summary>
    public async Task<IReadOnlyList<FixtureAssignmentView>> HistoryAsync(int fixtureId, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("app", "fixture_gameweek_assignments", ct)) return [];
        var rows = await db.Database.SqlQuery<AssignmentRow>($"""
            SELECT id AS "Id", fixture_id AS "FixtureId", gameweek_id AS "GameweekId", reason AS "Reason",
                   assigned_by_user_id AS "AssignedByUserId", created_at AS "CreatedAt"
            FROM app.fixture_gameweek_assignments WHERE fixture_id = {fixtureId} ORDER BY id DESC
            """).ToListAsync(ct);
        var ids = rows.Where(r => r.GameweekId is not null).Select(r => r.GameweekId!.Value).Distinct().ToList();
        var numbers = await PersistedNumbersAsync(ids, ct);
        return rows.Select(r => new FixtureAssignmentView(
            r.Id, r.FixtureId, r.GameweekId, r.GameweekId is { } g && numbers.TryGetValue(g, out var n) ? n : null,
            r.Reason, r.AssignedByUserId, r.CreatedAt)).ToList();
    }

    private Task InsertAssignmentAsync(int fixtureId, int? gameweekId, string reason, int? userId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO app.fixture_gameweek_assignments (fixture_id, gameweek_id, reason, assigned_by_user_id)
            VALUES ({fixtureId}, {gameweekId}, {reason}, {userId})
            """, ct);

    private async Task<Dictionary<int, int>> PersistedNumbersAsync(IReadOnlyCollection<int> gameweekIds, CancellationToken ct)
    {
        if (gameweekIds.Count == 0 || !await schema.HasColumnAsync("app", "gameweeks", "number", ct)) return [];
        var ids = gameweekIds.ToArray();
        var rows = await db.Database.SqlQuery<GameweekNumberRow>($"""
            SELECT id AS "Id", number AS "Number" FROM app.gameweeks WHERE id = ANY({ids})
            """).ToListAsync(ct);
        return rows.Where(r => r.Number is not null).ToDictionary(r => r.Id, r => (int)r.Number!.Value);
    }

    /// <summary>Newest assignment row per fixture. Empty until 007 is applied.</summary>
    private async Task<Dictionary<int, AssignmentRow>> CurrentAssignmentsAsync(IReadOnlyCollection<int> fixtureIds, CancellationToken ct)
    {
        if (fixtureIds.Count == 0 || !await schema.HasTableAsync("app", "fixture_gameweek_assignments", ct)) return [];
        var ids = fixtureIds.ToArray();
        var rows = await db.Database.SqlQuery<AssignmentRow>($"""
            SELECT DISTINCT ON (fixture_id)
                   id AS "Id", fixture_id AS "FixtureId", gameweek_id AS "GameweekId", reason AS "Reason",
                   assigned_by_user_id AS "AssignedByUserId", created_at AS "CreatedAt"
            FROM app.fixture_gameweek_assignments
            WHERE fixture_id = ANY({ids})
            ORDER BY fixture_id, id DESC
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.FixtureId);
    }
}
