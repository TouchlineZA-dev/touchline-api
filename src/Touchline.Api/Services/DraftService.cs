using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>Draft settings ("Drafts" section).</summary>
public sealed class DraftOptions
{
    public const string SectionName = "Drafts";

    /// <summary>A draft not saved for this many days is ignored (as if cleared).</summary>
    public int ExpiryDays { get; set; } = 14;

    /// <summary>Largest payload accepted, in bytes of JSON.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;
}

/// <summary>The wizards that can keep a draft.</summary>
public static class DraftKinds
{
    public const string Onboarding = "onboarding";
    public const string Squad = "squad";
    public const string Lineup = "lineup";
    public const string Transfers = "transfers";
    public const string League = "league";

    public static readonly IReadOnlyList<string> All = [Onboarding, Squad, Lineup, Transfers, League];
}

/// <param name="Payload">Whatever the app stored: the API never interprets it, scores it or treats it as a saved squad.</param>
/// <param name="Version">Send this back as "version" on the next save; a different version means another device saved first.</param>
public sealed record DraftView(string Kind, string? Step, JsonElement Payload, int Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record DraftSummary(string Kind, string? Step, int Version, DateTimeOffset UpdatedAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record DraftListView(IReadOnlyList<DraftSummary> Drafts, int ExpiryDays, DateTimeOffset ServerTimeUtc);

/// <param name="Payload">A JSON object (up to Drafts:MaxPayloadBytes).</param>
/// <param name="Version">The version last loaded, or 0 / null when starting a new draft.</param>
/// <param name="Step">Optional: the wizard step the manager was on (up to 50 characters), e.g. "pick_defenders".</param>
public sealed record SaveDraftRequest(JsonElement? Payload, int? Version = null, string? Step = null);

internal sealed class DraftRow
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string? Step { get; set; }
    public string Payload { get; set; } = "{}";
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
}

/// <summary>
/// Save-and-resume for wizards (docs/ui/wizard-contract.md, "Drafts"). One draft per manager and kind,
/// always the caller's own (the user comes from the token). Saving overwrites; a stale version gets 409
/// draft_stale with the current draft, so two devices never silently overwrite each other. Clearing is an
/// UPDATE (cleared_at), never a DELETE. Drafts are never read by squads, scoring or leagues.
/// Needs docs/migrations/015 (touchline-sync V051); before that every draft route is 503 drafts_unavailable.
/// </summary>
public sealed class DraftService(TouchlineDbContext db, SchemaFeatures schema, IOptions<DraftOptions> options, TimeProvider time)
{
    private readonly DraftOptions _options = options.Value;

    public Task<bool> AvailableAsync(CancellationToken ct) => schema.HasTableAsync("app", "wizard_drafts", ct);

    public async Task<DraftListView> ListAsync(AppUser user, CancellationToken ct)
    {
        await RequireAvailableAsync(ct);
        var now = time.GetUtcNow();
        var rows = await Rows(user.Id, null).ToListAsync(ct);
        var live = rows.Where(r => IsLive(r, now))
            .OrderBy(r => DraftKinds.All.ToList().IndexOf(r.Kind))
            .Select(r => new DraftSummary(r.Kind, r.Step, r.Version, r.UpdatedAt, r.ExpiresAt))
            .ToList();
        return new DraftListView(live, _options.ExpiryDays, now);
    }

    /// <summary>The kinds with a live draft; empty before the migration (never throws for that).</summary>
    public async Task<IReadOnlyList<string>> LiveKindsAsync(int userId, CancellationToken ct)
    {
        if (!await AvailableAsync(ct)) return [];
        var now = time.GetUtcNow();
        return (await Rows(userId, null).ToListAsync(ct)).Where(r => IsLive(r, now)).Select(r => r.Kind)
            .OrderBy(k => DraftKinds.All.ToList().IndexOf(k)).ToList();
    }

    public async Task<DraftView> GetAsync(AppUser user, string kind, CancellationToken ct)
    {
        kind = ValidKind(kind);
        await RequireAvailableAsync(ct);
        var row = await Rows(user.Id, kind).FirstOrDefaultAsync(ct);
        if (row is null || !IsLive(row, time.GetUtcNow())) throw NotFound(kind);
        return View(row);
    }

    public async Task<DraftView> SaveAsync(AppUser user, string kind, SaveDraftRequest request, CancellationToken ct)
    {
        kind = ValidKind(kind);
        var payload = ValidPayload(request.Payload);
        var step = request.Step?.Trim();
        if (step is { Length: 0 }) step = null;
        if (step is { Length: > 50 }) throw new RuleViolationException("draft_step", "The step name is at most 50 characters.");
        if (request.Version is < 0) throw new RuleViolationException("draft_version", "version can't be negative.");
        await RequireAvailableAsync(ct);

        var now = time.GetUtcNow();
        var expires = now.AddDays(_options.ExpiryDays);
        var expected = request.Version ?? 0;
        var row = await Rows(user.Id, kind).FirstOrDefaultAsync(ct);

        if (row is null)
        {
            if (expected != 0) throw Stale(kind, null);
            var inserted = await db.Database.SqlQueryRaw<DraftRow>($$"""
                INSERT INTO app.wizard_drafts (user_id, kind, step, payload, version, created_at, updated_at, expires_at)
                VALUES ({0}, {1}, {2}, CAST({3} AS jsonb), 1, {4}, {4}, {5})
                ON CONFLICT (user_id, kind) DO NOTHING
                RETURNING {{ColumnList}}
                """, user.Id, kind, Nullable(step), payload, now, expires).ToListAsync(ct);
            if (inserted.Count == 1) return View(inserted[0]);
            throw Stale(kind, await CurrentAsync(user.Id, kind, ct));   // another device created it at the same moment
        }

        var live = IsLive(row, now);
        var ok = live ? expected == row.Version : expected == 0 || expected == row.Version;
        if (!ok) throw Stale(kind, live ? View(row) : null);

        // The version in the WHERE makes the check and the write one step: a save that raced another loses here.
        var updated = await db.Database.SqlQueryRaw<DraftRow>($$"""
            UPDATE app.wizard_drafts
               SET payload = CAST({0} AS jsonb), step = {1}, version = version + 1, updated_at = {2}, expires_at = {3},
                   cleared_at = NULL, created_at = CASE WHEN {4} THEN created_at ELSE {2} END
             WHERE id = {5} AND version = {6}
            RETURNING {{ColumnList}}
            """, payload, Nullable(step), now, expires, live, row.Id, row.Version).ToListAsync(ct);
        if (updated.Count == 1) return View(updated[0]);
        throw Stale(kind, await CurrentAsync(user.Id, kind, ct));
    }

    /// <summary>Discard a draft: an UPDATE that sets cleared_at and empties the payload. Clearing nothing is fine (idempotent).</summary>
    public async Task ClearAsync(AppUser user, string kind, int? version, CancellationToken ct)
    {
        kind = ValidKind(kind);
        await RequireAvailableAsync(ct);
        var now = time.GetUtcNow();
        var row = await Rows(user.Id, kind).FirstOrDefaultAsync(ct);
        if (row is null || !IsLive(row, now)) return;
        if (version is { } v && v != row.Version) throw Stale(kind, View(row));
        await db.Database.ExecuteSqlAsync($"""
            UPDATE app.wizard_drafts SET cleared_at = {now}, payload = jsonb_build_object(), step = NULL, version = version + 1, updated_at = {now}
            WHERE id = {row.Id} AND cleared_at IS NULL
            """, ct);
    }

    /// <summary>Account deletion: clear every draft (UPDATE, no DELETE). No-op before the migration.</summary>
    public async Task ClearAllAsync(int userId, CancellationToken ct)
    {
        if (!await AvailableAsync(ct)) return;
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE app.wizard_drafts SET cleared_at = {now}, payload = jsonb_build_object(), step = NULL, version = version + 1, updated_at = {now}
            WHERE user_id = {userId} AND cleared_at IS NULL
            """, ct);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private const string ColumnList = """
        id AS "Id", kind AS "Kind", step AS "Step", payload::text AS "Payload", version AS "Version",
        created_at AS "CreatedAt", updated_at AS "UpdatedAt", expires_at AS "ExpiresAt", cleared_at AS "ClearedAt"
        """;

    /// <summary>A null parameter for raw SQL needs a typed DBNull.</summary>
    private static object Nullable(string? value) => value is null
        ? new Npgsql.NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }
        : value;

    private IQueryable<DraftRow> Rows(int userId, string? kind) => kind is null
        ? db.Database.SqlQueryRaw<DraftRow>($"SELECT {ColumnList} FROM app.wizard_drafts WHERE user_id = {{0}}", userId)
        : db.Database.SqlQueryRaw<DraftRow>($"SELECT {ColumnList} FROM app.wizard_drafts WHERE user_id = {{0}} AND kind = {{1}}", userId, kind);

    private async Task<DraftView?> CurrentAsync(int userId, string kind, CancellationToken ct)
    {
        var row = await Rows(userId, kind).FirstOrDefaultAsync(ct);
        return row is not null && IsLive(row, time.GetUtcNow()) ? View(row) : null;
    }

    private static bool IsLive(DraftRow row, DateTimeOffset now) => row.ClearedAt is null && row.ExpiresAt > now;

    private static DraftView View(DraftRow r)
    {
        using var doc = JsonDocument.Parse(r.Payload);
        return new DraftView(r.Kind, r.Step, doc.RootElement.Clone(), r.Version, r.CreatedAt, r.UpdatedAt, r.ExpiresAt);
    }

    private async Task RequireAvailableAsync(CancellationToken ct)
    {
        if (!await AvailableAsync(ct))
        {
            throw new CodedException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.DraftsUnavailable, "Drafts unavailable",
                "Drafts aren't available yet (docs/migrations/015, touchline-sync V051, isn't applied). Everything else works; keep the draft on the device for now.");
        }
    }

    private static string ValidKind(string? kind)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        if (!DraftKinds.All.Contains(k))
        {
            throw new RuleViolationException("draft_kind", $"'{kind}' isn't a draft kind. Use one of {string.Join(", ", DraftKinds.All)}.");
        }
        return k;
    }

    private string ValidPayload(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p)
        {
            throw new RuleViolationException("draft_payload", "Send the draft as a JSON object in \"payload\".");
        }
        var json = p.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > _options.MaxPayloadBytes)
        {
            throw new RuleViolationException("draft_payload", $"A draft can be at most {_options.MaxPayloadBytes / 1024} KB.");
        }
        return json;
    }

    private static CodedException NotFound(string kind) => new(StatusCodes.Status404NotFound, ErrorCodes.DraftNotFound, "No draft",
        $"You have no {kind} draft (none saved, discarded, or expired).");

    private static ConflictException Stale(string kind, DraftView? current) => new(
        current is null
            ? $"Your {kind} draft was discarded or replaced on another device, so this save wasn't applied. Start again from 'version': 0, or reload."
            : $"Your {kind} draft was saved on another device since you loaded it, so this save wasn't applied. 'current' has the newer draft.",
        "Draft changed", current is null ? new { kind, draft = (DraftView?)null } : current, ErrorCodes.DraftStale);

}
