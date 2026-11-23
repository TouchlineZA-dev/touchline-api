using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Services;

/// <summary>Settlement settings ("Settlement" section).</summary>
public sealed class SettlementOptions
{
    public const string SectionName = "Settlement";

    /// <summary>An unattributed goal or card keeps its fixture under review this long after full time (default decision 4).</summary>
    public int UnattributedHoldHours { get; set; } = 48;

    /// <summary>Teams scored per committed batch while a revision is being built.</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>Gameweek states (03-Leruo-API Stage 5.1).</summary>
public static class SettlementStates
{
    public const string Open = "open";
    public const string Locked = "locked";
    public const string Live = "live";
    public const string AwaitingReview = "awaiting_review";
    public const string Final = "final";

    private static readonly string[] Order = [Open, Locked, Live, AwaitingReview, Final];

    /// <summary>Forward only, except a reopened fixture (awaiting_review → live). Final is terminal (amendments stay final).</summary>
    public static bool IsValid(string? from, string to) =>
        from is null || from == to || (from == AwaitingReview && to == Live) || (from != Final && Array.IndexOf(Order, to) > Array.IndexOf(Order, from));
}

/// <summary>Per-fixture completeness outcomes (03-Leruo-API Stage 5.2).</summary>
public static class FixtureOutcomes
{
    public const string NotFinished = "not_finished";
    public const string Complete = "complete";                       // worker evidence (gameweek_sync_status)
    public const string VerifiedZero = "verified_zero";              // admin: a genuinely quiet match
    public const string ManualResolution = "manual_resolution";      // admin: recorded, authorised decision
    public const string UnattributedResolved = "unattributed_resolved";
    public const string UnattributedHold = "unattributed_hold";      // within the hold window
    public const string AwaitingEvidence = "awaiting_evidence";      // finished, no worker evidence, no resolution: delayed feed
    public const string MissingData = "missing_data";                // finished with no lineups and no events
    public const string LateFacts = "late_facts";                    // facts changed after the admin resolved it
    public const string Postponed = "postponed", Cancelled = "cancelled", Abandoned = "abandoned", Deleted = "deleted";
    public const string Awarded = "awarded", WalkOver = "walk_over";

    public static readonly IReadOnlySet<string> ResolutionOutcomes = new HashSet<string> { VerifiedZero, ManualResolution, UnattributedResolved };
}

public sealed record FixtureCompleteness(int FixtureId, string Home, string Away, short? StateId, string Outcome, bool Blocking, string Detail,
    string Revision, int Events, int Lineups, int UnattributedEvents);

public sealed record RevisionView(int Id, string Kind, string Status, string InputRevision, int? PreviousRevisionId, string RulesVersion,
    string? Source, string? Reason, string? CorrectionMessage, int? AuthorUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset? PublishedAtUtc);

public sealed record SettlementView(
    int GameweekId,
    int Number,
    string State,
    string StoredState,
    string Reason,
    bool CanFinalise,
    IReadOnlyList<string> Blockers,
    bool WorkerMarkedComplete,
    string InputRevision,
    bool AmendmentNeeded,
    RevisionView? PublishedRevision,
    IReadOnlyList<FixtureCompleteness> Fixtures,
    DateTimeOffset ServerTimeUtc);

public sealed record ResolveFixtureRequest(string Outcome, string Reason);

public sealed record CorrectionRequest(string Source, string Reason, int PreviousRevisionId);

internal sealed class RevisionRow
{
    public int Id { get; set; }
    public int GameweekId { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public string InputRevision { get; set; } = "";
    public int? PreviousRevisionId { get; set; }
    public string RulesVersion { get; set; } = "";
    public string? Source { get; set; }
    public string? Reason { get; set; }
    public string? CorrectionMessage { get; set; }
    public int? AuthorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public RevisionView View() => new(Id, Kind, Status, InputRevision, PreviousRevisionId, RulesVersion, Source, Reason, CorrectionMessage, AuthorUserId, CreatedAt, PublishedAt);
}

internal sealed class ResolutionRow
{
    public int FixtureId { get; set; }
    public string Outcome { get; set; } = "";
    public string FixtureRevision { get; set; } = "";
}

internal sealed class RevisionTeamRow
{
    public int FantasyTeamId { get; set; }
    public int Total { get; set; }
    public string Breakdown { get; set; } = "";
}

internal sealed class StateRow
{
    public string State { get; set; } = "";
    public int? PublishedRevisionId { get; set; }
}

/// <summary>A team's scored gameweek, as stored in a revision (and returned by the explain endpoint).</summary>
public sealed record TeamScoreBreakdown(
    int FantasyTeamId,
    string? Chip,
    int TransferCost,
    int PointsBeforeHits,
    int BenchPoints,
    int Total,
    IReadOnlyList<PickBreakdown> Picks);

public sealed record PickBreakdown(
    int PlayerId,
    bool IsStarter,
    int? BenchOrder,
    bool IsCaptain,
    bool IsViceCaptain,
    int Multiplier,
    bool Counted,
    int Contribution,
    string Reason,
    PointsBreakdown Points,
    IReadOnlyList<FixturePoints> Fixtures);

public sealed record FixturePoints(int FixtureId, PointsBreakdown Points);

/// <summary>
/// Settlement (03-Leruo-API Stage 5, handbook Gate 7): the gameweek state machine, the completeness
/// contract, manual resolutions, final and amendment revisions built in batches and published
/// atomically, corrections with impact preview, and the explanation view. Needs migration 011; before
/// it, finalising requires the worker's marked_complete evidence and uses the legacy path.
/// </summary>
public sealed class SettlementService(
    TouchlineDbContext db,
    GameweekService gameweeks,
    MatchFactsService facts,
    SquadStore squads,
    SchemaFeatures schema,
    IMemoryCache cache,
    IOptions<FantasyRules> rules,
    IOptions<SettlementOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly FantasyRules _rules = rules.Value;
    private readonly SettlementOptions _options = options.Value;
    private readonly PlayerScoring _playerScoring = new(rules.Value);
    private readonly TeamScoring _teamScoring = new(rules.Value);

    public Task<bool> ReadyAsync(CancellationToken ct) => schema.HasTableAsync("app", "score_revisions", ct);

    // ---- Completeness and state -------------------------------------------------------------------

    public async Task<SettlementView> EvaluateAsync(int gameweekId, DateTimeOffset now, CancellationToken ct)
    {
        var (info, _) = await GameweekAsync(gameweekId, ct);
        var fixtureIds = info.Fixtures.Select(f => f.Id).ToList();
        var events = await db.MatchEvents.Where(e => fixtureIds.Contains(e.FixtureId)).ToListAsync(ct);
        var lineups = await db.PlayerLineups.Where(l => fixtureIds.Contains(l.FixtureId)).ToListAsync(ct);
        var known = (await db.Players
                .Where(p => events.Select(e => e.SportmonksPlayerId).Contains(p.SportmonksPlayerId))
                .Select(p => p.SportmonksPlayerId).ToListAsync(ct)).ToHashSet();
        var teamIds = info.Fixtures.SelectMany(f => new[] { f.HomeTeamId, f.AwayTeamId }).Where(t => t is not null).Select(t => t!.Value).Distinct().ToList();
        var names = await db.Teams.Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var marked = await gameweeks.IsMarkedCompleteAsync(gameweekId, ct);
        var ready = await ReadyAsync(ct);
        var resolutions = ready ? await ResolutionsAsync(fixtureIds, ct) : [];

        var outcomes = new List<FixtureCompleteness>();
        foreach (var f in info.Fixtures)
        {
            var fEvents = MatchFactsDecoder.DistinctEvents(events.Where(e => e.FixtureId == f.Id)).ToList();
            var fLineups = lineups.Where(l => l.FixtureId == f.Id).ToList();
            var revision = MatchDetailService.Revision(f, fEvents, fLineups);
            var live = fEvents.Where(e => e.Rescinded != true).ToList();
            var unattributed = live.Count(e => e.TypeId is Codes.EventTypes.Goal or Codes.EventTypes.PenaltyScored or Codes.EventTypes.OwnGoal
                    or Codes.EventTypes.YellowCard or Codes.EventTypes.RedCard or Codes.EventTypes.SecondYellowRed
                && (e.SportmonksPlayerId is null || !known.Contains(e.SportmonksPlayerId.Value)));
            var (outcome, blocking, detail) = Outcome(f, live.Count, fLineups.Count, unattributed, marked, resolutions.GetValueOrDefault(f.Id), revision, now);
            string NameOf(int? id) => id is { } t && names.TryGetValue(t, out var n) ? n : "TBD";
            outcomes.Add(new FixtureCompleteness(f.Id, NameOf(f.HomeTeamId), NameOf(f.AwayTeamId), f.StateId, outcome, blocking, detail, revision, live.Count, fLineups.Count, unattributed));
        }

        var inputRevision = InputRevision(outcomes);
        var blockers = outcomes.Where(o => o.Blocking).Select(o => $"Fixture {o.FixtureId} ({o.Home} v {o.Away}): {o.Detail}").ToList();
        if (info.Fixtures.Count == 0) blockers.Add("The gameweek has no fixtures.");

        var stored = ready ? await StoredStateAsync(gameweekId, ct) : null;
        var published = stored?.PublishedRevisionId is { } pid ? await RevisionAsync(pid, ct) : null;
        var derived = published is not null ? SettlementStates.Final
            : !info.IsLocked(now) ? SettlementStates.Open
            : info.Fixtures.All(f => f.StartingAt is null || f.StartingAt > now) && outcomes.All(o => o.Outcome == FixtureOutcomes.NotFinished) ? SettlementStates.Locked
            : outcomes.Any(o => o.Outcome == FixtureOutcomes.NotFinished) ? SettlementStates.Live
            : SettlementStates.AwaitingReview;
        var reason = derived switch
        {
            SettlementStates.Open => "Before the deadline.",
            SettlementStates.Locked => "Deadline passed; no fixture has kicked off.",
            SettlementStates.Live => "Fixtures are being played.",
            SettlementStates.AwaitingReview => blockers.Count == 0 ? "Every fixture is complete or resolved; ready to finalise." : "Waiting for completeness evidence or an admin resolution.",
            _ => $"Published {published!.Kind} revision {published.Id}.",
        };

        return new SettlementView(gameweekId, info.Number, derived, stored?.State ?? "none", reason,
            derived == SettlementStates.AwaitingReview && blockers.Count == 0, blockers, marked, inputRevision,
            published is not null && published.InputRevision != inputRevision, published, outcomes, now);
    }

    private (string Outcome, bool Blocking, string Detail) Outcome(
        Fixture f, int events, int lineups, int unattributed, bool marked, ResolutionRow? resolution, string revision, DateTimeOffset now)
    {
        var state = MatchStateMatrix.For(f.StateId);
        if (!state.Played && state.Settled)
        {
            return state.Phase switch
            {
                MatchPhase.Awarded => (FixtureOutcomes.Awarded, false, "Awarded: the result stands, no player points."),
                MatchPhase.WalkOver => (FixtureOutcomes.WalkOver, false, "Walk-over: the result stands, no player points."),
                MatchPhase.Cancelled => (FixtureOutcomes.Cancelled, false, "Cancelled: scores nothing."),
                MatchPhase.Abandoned => (FixtureOutcomes.Abandoned, false, "Abandoned: scores nothing this gameweek."),
                MatchPhase.Deleted => (FixtureOutcomes.Deleted, false, "Deleted by the provider: scores nothing."),
                _ => (FixtureOutcomes.Postponed, false, "Postponed: the gameweek doesn't wait; reassign it when rescheduled."),
            };
        }
        if (!(state.Played && state.ResultFinal)) return (FixtureOutcomes.NotFinished, true, $"Not finished ({state.Code}).");

        var fullTime = (f.StartingAt ?? now).AddMinutes((f.Length ?? _rules.DefaultMatchLengthMinutes) + _rules.HalfTimeBreakMinutes);
        if (unattributed > 0 && now < fullTime.AddHours(_options.UnattributedHoldHours) && resolution?.Outcome != FixtureOutcomes.UnattributedResolved)
        {
            return (FixtureOutcomes.UnattributedHold, true,
                $"{unattributed} goal/card event(s) without an identifiable player; held until {fullTime.AddHours(_options.UnattributedHoldHours):yyyy-MM-dd HH:mm} UTC for a later sync.");
        }
        if (resolution is not null)
        {
            return resolution.FixtureRevision == revision
                ? (resolution.Outcome, false, $"Resolved by an admin ({resolution.Outcome}).")
                : (FixtureOutcomes.LateFacts, true, "The match facts changed after the admin resolution; review and resolve again.");
        }
        if (marked) return (FixtureOutcomes.Complete, false, "The sync worker marked the gameweek complete.");
        if (events == 0 && lineups == 0) return (FixtureOutcomes.MissingData, true, "Finished, but no lineups or events are stored.");
        if (unattributed > 0) return (FixtureOutcomes.AwaitingEvidence, true, $"Finished; {unattributed} unattributed event(s) and no completeness evidence. An admin can resolve it.");
        return (FixtureOutcomes.AwaitingEvidence, true,
            events == 0 ? "Finished with no events: a quiet match or a delayed feed. An admin can record verified_zero." : "Finished; waiting for the worker's completeness evidence or an admin resolution.");
    }

    private static string InputRevision(IEnumerable<FixtureCompleteness> outcomes) =>
        "in-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', outcomes.OrderBy(o => o.FixtureId).Select(o => $"{o.FixtureId}:{o.Revision}:{o.Outcome}")))))[..16].ToLowerInvariant();

    /// <summary>Admin: re-evaluate and record the transition if the state moved (with reason and input revision).</summary>
    public async Task<SettlementView> RecordStateAsync(int gameweekId, int? actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        await RequireReadyAsync(ct);
        var view = await EvaluateAsync(gameweekId, now, ct);
        await TransitionAsync(gameweekId, view.StoredState == "none" ? null : view.StoredState, view.State, view.Reason, view.InputRevision, actorUserId, null, ct);
        return view with { StoredState = view.State };
    }

    private async Task TransitionAsync(int gameweekId, string? from, string to, string reason, string inputRevision, int? actor, int? publishedRevisionId, CancellationToken ct)
    {
        if (from == to && publishedRevisionId is null) return;
        if (!SettlementStates.IsValid(from, to))
        {
            throw new ConflictException($"A gameweek can't move from {from} to {to}.", "Invalid transition", code: "invalid_transition");
        }
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO app.gameweek_settlement (gameweek_id, state, reason, input_revision, published_revision_id, updated_at)
            VALUES ({gameweekId}, {to}, {reason}, {inputRevision}, {publishedRevisionId}, now())
            ON CONFLICT (gameweek_id) DO UPDATE SET state = EXCLUDED.state, reason = EXCLUDED.reason, input_revision = EXCLUDED.input_revision,
                published_revision_id = COALESCE(EXCLUDED.published_revision_id, app.gameweek_settlement.published_revision_id), updated_at = now()
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO app.gameweek_state_transitions (gameweek_id, from_state, to_state, reason, input_revision, actor_user_id)
            VALUES ({gameweekId}, {from}, {to}, {reason}, {inputRevision}, {actor})
            """, ct);
    }

    /// <summary>Admin: record an outcome for a finished fixture without worker evidence (recorded, authorised, tied to the facts it saw).</summary>
    public async Task<SettlementView> ResolveFixtureAsync(int fixtureId, ResolveFixtureRequest request, int actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        await RequireReadyAsync(ct);
        if (!FixtureOutcomes.ResolutionOutcomes.Contains(request.Outcome))
        {
            throw new RuleViolationException($"Outcome must be one of {string.Join(", ", FixtureOutcomes.ResolutionOutcomes)}.");
        }
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RuleViolationException("Every resolution needs a reason.");
        var fixture = await db.Fixtures.FirstOrDefaultAsync(f => f.Id == fixtureId, ct) ?? throw new NotFoundException($"Fixture {fixtureId} doesn't exist.");
        var calendar = await gameweeks.GetCalendarAsync(fixture.SeasonId, ct);
        var gameweek = calendar.ForFixture(fixtureId) ?? throw new RuleViolationException("The fixture isn't in any gameweek.");
        var before = await EvaluateAsync(gameweek.Id, now, ct);
        var current = before.Fixtures.Single(x => x.FixtureId == fixtureId);
        if (current.Outcome == FixtureOutcomes.NotFinished) throw new ConflictException("Only a finished fixture can be resolved.", "Not finished", code: "settlement_blocked");
        if (current.Outcome == FixtureOutcomes.UnattributedHold && request.Outcome != FixtureOutcomes.UnattributedResolved)
        {
            throw new ConflictException("This fixture has unattributed goals or cards; resolve it as unattributed_resolved once the hold window has passed.",
                "Under review", code: "settlement_blocked");
        }
        if (request.Outcome == FixtureOutcomes.UnattributedResolved && current.Outcome == FixtureOutcomes.UnattributedHold)
        {
            throw new ConflictException(current.Detail, "Hold window", code: "settlement_blocked");
        }
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO app.fixture_resolutions (fixture_id, outcome, reason, fixture_revision, actor_user_id)
            VALUES ({fixtureId}, {request.Outcome}, {request.Reason.Trim()}, {current.Revision}, {actorUserId})
            """, ct);
        return await EvaluateAsync(gameweek.Id, now, ct);
    }

    // ---- Revisions -------------------------------------------------------------------------------

    /// <summary>
    /// Admin: finalise. Only from awaiting_review with every fixture complete or resolved — never on
    /// "all fixtures final" alone. Builds the final revision in batches (resuming an interrupted run),
    /// then publishes it atomically.
    /// </summary>
    public async Task<object> FinaliseAsync(int gameweekId, int? actorUserId, DateTimeOffset now, CancellationToken ct, Func<int, bool>? interruptAfterBatch = null)
    {
        await RequireReadyAsync(ct);
        var view = await EvaluateAsync(gameweekId, now, ct);
        if (view.State == SettlementStates.Final)
        {
            throw new ConflictException("This gameweek is already final. Use the corrections workflow to amend it.", "Already final", code: "already_final");
        }
        if (!view.CanFinalise)
        {
            throw new ConflictException($"The gameweek can't be finalised yet ({view.State}).", "Not ready", view, "settlement_blocked");
        }
        await TransitionAsync(gameweekId, view.StoredState == "none" ? null : view.StoredState, SettlementStates.AwaitingReview, view.Reason, view.InputRevision, actorUserId, null, ct);
        var revision = await BuildAndPublishAsync(gameweekId, "final", view.InputRevision, null, null, null, null, actorUserId, now, ct, interruptAfterBatch);
        return new { gameweekId, gameweek = view.Number, revision, rulesVersion = Rulebook.Version, finalisedAtUtc = now };
    }

    /// <summary>Admin: what a correction would change — players, teams and overall ranks — without publishing.</summary>
    public async Task<object> PreviewCorrectionAsync(int gameweekId, DateTimeOffset now, CancellationToken ct)
    {
        await RequireReadyAsync(ct);
        var view = await EvaluateAsync(gameweekId, now, ct);
        var published = view.PublishedRevision ?? throw new ConflictException("Only a final gameweek can be corrected.", "Not final", code: "not_final");
        var (info, calendar) = await GameweekAsync(gameweekId, ct);
        var before = (await RevisionTeamsAsync(published.Id, ct)).ToDictionary(t => t.FantasyTeamId);
        var after = await ScoreTeamsAsync(info, calendar, before.Keys.ToList(), now, ct);

        var teams = after.Values
            .Select(a => (After: a, Before: before.TryGetValue(a.FantasyTeamId, out var b) ? JsonSerializer.Deserialize<TeamScoreBreakdown>(b.Breakdown, Json)! : null))
            .Where(x => x.Before is null || x.Before.Total != x.After.Total)
            .Select(x => new
            {
                fantasyTeamId = x.After.FantasyTeamId,
                before = x.Before?.Total,
                after = x.After.Total,
                delta = x.After.Total - (x.Before?.Total ?? 0),
                players = x.After.Picks
                    .Select(p => (p, old: x.Before?.Picks.FirstOrDefault(o => o.PlayerId == p.PlayerId)))
                    .Where(t => t.old is null || t.old.Points.Total != t.p.Points.Total || t.old.Contribution != t.p.Contribution)
                    .Select(t => new { t.p.PlayerId, before = t.old?.Points.Total, after = t.p.Points.Total, contributionBefore = t.old?.Contribution, contributionAfter = t.p.Contribution })
                    .ToList(),
            })
            .ToList();

        var seasonTotals = await SeasonTotalsAsync(info.Gameweek.SeasonId!.Value, ct);
        int RankOf(Dictionary<int, int> totals, int teamId) => totals.Count(kv => kv.Value > totals[teamId]) + 1;
        var corrected = seasonTotals.ToDictionary(kv => kv.Key, kv => kv.Value - (before.TryGetValue(kv.Key, out var b) ? b.Total : 0) + (after.TryGetValue(kv.Key, out var a) ? a.Total : 0));
        var rankChanges = seasonTotals.Keys
            .Select(id => new { fantasyTeamId = id, before = RankOf(seasonTotals, id), after = RankOf(corrected, id) })
            .Where(r => r.before != r.after).ToList();

        return new
        {
            gameweekId, gameweek = info.Number, currentRevision = published, newInputRevision = view.InputRevision,
            factsChanged = view.AmendmentNeeded, teamsChanged = teams.Count, teams, overallRankChanges = rankChanges, previewedAtUtc = now,
        };
    }

    /// <summary>Admin: publish an amendment revision with source, author, reason and the previous revision, and a correction message.</summary>
    public async Task<object> PublishCorrectionAsync(int gameweekId, CorrectionRequest request, int actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        await RequireReadyAsync(ct);
        if (string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new RuleViolationException("A correction needs a source and a reason.");
        }
        var view = await EvaluateAsync(gameweekId, now, ct);
        var published = view.PublishedRevision ?? throw new ConflictException("Only a final gameweek can be corrected.", "Not final", code: "not_final");
        if (published.Id != request.PreviousRevisionId)
        {
            throw new ConflictException($"The published revision is {published.Id}, not {request.PreviousRevisionId}. Preview again.", "Revision changed", code: ErrorCodes.StaleSquad);
        }
        var message = $"Gameweek {view.Number} scores were corrected: {request.Reason.Trim()}";
        var revision = await BuildAndPublishAsync(gameweekId, "amendment", view.InputRevision, published.Id, request.Source.Trim(), request.Reason.Trim(), message, actorUserId, now, ct, null);
        return new { gameweekId, gameweek = view.Number, revision, previousRevisionId = published.Id, correctionMessage = message, publishedAtUtc = now };
    }

    private async Task<RevisionView> BuildAndPublishAsync(
        int gameweekId, string kind, string inputRevision, int? previousRevisionId, string? source, string? reason, string? message,
        int? actor, DateTimeOffset now, CancellationToken ct, Func<int, bool>? interruptAfterBatch)
    {
        var (info, calendar) = await GameweekAsync(gameweekId, ct);

        // Resume a building revision for the same inputs, or abandon stale ones and start afresh.
        var building = await db.Database.SqlQuery<int>($"""
            SELECT id AS "Value" FROM app.score_revisions
            WHERE gameweek_id = {gameweekId} AND kind = {kind} AND status = 'building' AND input_revision = {inputRevision}
            ORDER BY id DESC LIMIT 1
            """).ToListAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE app.score_revisions SET status = 'abandoned'
            WHERE gameweek_id = {gameweekId} AND status = 'building' AND NOT (kind = {kind} AND input_revision = {inputRevision})
            """, ct);
        var revisionId = building.Count > 0 ? building[0] : (await db.Database.SqlQuery<int>($"""
            INSERT INTO app.score_revisions (gameweek_id, kind, status, input_revision, previous_revision_id, rules_version, source, reason, correction_message, author_user_id)
            VALUES ({gameweekId}, {kind}, 'building', {inputRevision}, {previousRevisionId}, {Rulebook.Version}, {source}, {reason}, {message}, {actor})
            RETURNING id AS "Value"
            """).ToListAsync(ct)).Single();

        var done = (await db.Database.SqlQuery<int>($"SELECT fantasy_team_id AS \"Value\" FROM app.score_revision_teams WHERE revision_id = {revisionId}").ToListAsync(ct)).ToHashSet();
        var keptUnderOtherRules = (await db.Database.SqlQuery<int>($"""
            SELECT fantasy_team_id AS "Value" FROM app.fantasy_gameweek_entries
            WHERE gameweek_id = {gameweekId} AND scored_at IS NOT NULL AND rules_version IS NOT NULL AND rules_version <> {Rulebook.Version}
            """).ToListAsync(ct)).ToHashSet();
        var teamIds = await db.FantasyTeams.Where(t => t.SeasonId == info.Gameweek.SeasonId).OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
        var todo = teamIds.Where(id => !done.Contains(id) && !keptUnderOtherRules.Contains(id)).ToList();

        var batch = 0;
        foreach (var chunk in todo.Chunk(Math.Max(1, _options.BatchSize)))
        {
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                foreach (var teamId in chunk) await squads.LockAsync(teamId, info, calendar, now, ct);
                await tx.CommitAsync(ct);
            }
            var scored = await ScoreTeamsAsync(info, calendar, chunk, now, ct);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                foreach (var team in scored.Values)
                {
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO app.score_revision_teams (revision_id, fantasy_team_id, total, points_before_hits, bench_points, transfer_cost, breakdown)
                        VALUES ({revisionId}, {team.FantasyTeamId}, {team.Total}, {team.PointsBeforeHits}, {team.BenchPoints}, {team.TransferCost},
                                CAST({JsonSerializer.Serialize(team, Json)} AS jsonb))
                        ON CONFLICT (revision_id, fantasy_team_id) DO NOTHING
                        """, ct);
                }
                await tx.CommitAsync(ct);
            }
            batch++;
            if (interruptAfterBatch?.Invoke(batch) == true)
            {
                throw new OperationCanceledException($"Settlement interrupted after batch {batch} (test seam).");
            }
        }

        // Publish atomically: entries, points rows, revision status and state together.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var rows = await RevisionTeamsAsync(revisionId, ct);
            foreach (var row in rows)
            {
                var team = JsonSerializer.Deserialize<TeamScoreBreakdown>(row.Breakdown, Json)!;
                var existing = await db.PointsResults.AsTracking()
                    .Where(r => r.FantasyTeamId == team.FantasyTeamId && r.GameweekId == gameweekId).ToDictionaryAsync(r => r.PlayerId, ct);
                foreach (var pick in team.Picks)
                {
                    if (!existing.TryGetValue(pick.PlayerId, out var result))
                    {
                        result = new PointsResult { FantasyTeamId = team.FantasyTeamId, PlayerId = pick.PlayerId, GameweekId = gameweekId };
                        db.PointsResults.Add(result);
                    }
                    result.Points = pick.Points.Total;
                    result.Breakdown = PointsBreakdownJson.Write(pick.Points, new PickScore(pick.PlayerId, pick.Points.Total, pick.Multiplier, pick.Counted, pick.Contribution, pick.Reason));
                }
                var entry = await db.GameweekEntries.AsTracking().SingleAsync(e => e.FantasyTeamId == team.FantasyTeamId && e.GameweekId == gameweekId, ct);
                entry.Points = team.Total;
                entry.BenchPoints = team.BenchPoints;
                entry.ScoredAt = now;
            }
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE app.fantasy_gameweek_entries SET rules_version = {Rulebook.Version}, points_table_version = {Rulebook.PointsTableVersion}
                WHERE gameweek_id = {gameweekId} AND fantasy_team_id IN (SELECT fantasy_team_id FROM app.score_revision_teams WHERE revision_id = {revisionId})
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE app.points_results SET rules_version = {Rulebook.Version}, points_table_version = {Rulebook.PointsTableVersion}
                WHERE gameweek_id = {gameweekId} AND fantasy_team_id IN (SELECT fantasy_team_id FROM app.score_revision_teams WHERE revision_id = {revisionId})
                """, ct);
            await db.Database.ExecuteSqlAsync($"UPDATE app.score_revisions SET status = 'published', published_at = {now} WHERE id = {revisionId}", ct);
            var stored = await StoredStateAsync(gameweekId, ct);
            await TransitionAsync(gameweekId, stored?.State, SettlementStates.Final,
                kind == "final" ? $"Final revision {revisionId} published." : $"Amendment revision {revisionId} published: {reason}",
                inputRevision, actor, revisionId, ct);
            await tx.CommitAsync(ct);
        }
        cache.Remove($"fantasy:gw-points:{gameweekId}");
        return (await RevisionAsync(revisionId, ct))!;
    }

    /// <summary>Scores teams per fixture, then aggregates (03 Stage 5.3), with multipliers and their reasons.</summary>
    public async Task<Dictionary<int, TeamScoreBreakdown>> ScoreTeamsAsync(GameweekInfo info, SeasonCalendar calendar, IReadOnlyCollection<int> teamIds, DateTimeOffset now, CancellationToken ct)
    {
        var all = await facts.LoadAsync(info.Fixtures, now, ct);
        var perFixture = all.GroupBy(f => f.PlayerId).ToDictionary(g => g.Key,
            g => g.Select(x => new FixturePoints(x.Facts.FixtureId, _playerScoring.ScoreFixture(x.Facts, x.Position))).ToList());

        var result = new Dictionary<int, TeamScoreBreakdown>();
        foreach (var teamId in teamIds)
        {
            var squad = await squads.LoadAsync(teamId, info, calendar, ct);
            if (squad is null) continue;
            var selection = CaptaincyRules.EnsureCaptains(squad.ToSelection(), teamId, info.Id);
            var picks = squad.Picks.Select(p =>
            {
                var fixtures = perFixture.GetValueOrDefault(p.PlayerId) ?? [];
                var points = fixtures.Aggregate(PointsBreakdown.None, (sum, x) => sum.Add(x.Points));
                return (Pick: p, Points: points, Fixtures: fixtures);
            }).ToList();
            var score = _teamScoring.Score(
                picks.Select(x => new TeamPickPoints(x.Pick.PlayerId, !x.Pick.IsOnBench, x.Pick.PlayerId == selection.CaptainId, x.Pick.PlayerId == selection.ViceCaptainId, x.Points)).ToList(),
                squad.Entry.Chip, squad.Entry.TransferCost);
            result[teamId] = new TeamScoreBreakdown(teamId, squad.Entry.Chip, score.TransferCost, score.PointsBeforeHits, score.BenchPoints, score.Total,
                picks.Select(x =>
                {
                    var s = score.Picks.Single(p => p.PlayerId == x.Pick.PlayerId);
                    return new PickBreakdown(x.Pick.PlayerId, !x.Pick.IsOnBench, x.Pick.BenchOrder, x.Pick.PlayerId == selection.CaptainId, x.Pick.PlayerId == selection.ViceCaptainId,
                        s.Multiplier, s.Counted, s.Contribution, s.Reason, x.Points, x.Fixtures);
                }).ToList());
        }
        return result;
    }

    // ---- Explain ---------------------------------------------------------------------------------

    /// <summary>
    /// GET /fantasy/team/points/explain: every player's points per fixture, multipliers with reasons,
    /// bench, hits, the revision used, and what changed since the previous revision ("why did my score drop?").
    /// </summary>
    public async Task<object> ExplainAsync(FantasyTeam team, GameweekInfo info, SeasonCalendar calendar, DateTimeOffset now, CancellationToken ct)
    {
        TeamScoreBreakdown? current = null;
        TeamScoreBreakdown? previous = null;
        RevisionView? revision = null;
        if (await ReadyAsync(ct) && (await StoredStateAsync(info.Id, ct))?.PublishedRevisionId is { } pid)
        {
            revision = await RevisionAsync(pid, ct);
            current = await TeamBreakdownAsync(pid, team.Id, ct);
            if (revision?.PreviousRevisionId is { } prev) previous = await TeamBreakdownAsync(prev, team.Id, ct);
        }
        current ??= (await ScoreTeamsAsync(info, calendar, [team.Id], now, ct)).GetValueOrDefault(team.Id)
            ?? throw new NotFoundException("Your team has no squad for that gameweek.");

        var ids = current.Picks.Select(p => p.PlayerId).ToList();
        var names = await db.Players.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, FootballService.NameOf, ct);
        var changes = previous is null ? [] : current.Picks
            .Select(p => (p, old: previous.Picks.FirstOrDefault(o => o.PlayerId == p.PlayerId)))
            .Where(t => t.old is null || t.old.Contribution != t.p.Contribution)
            .Select(t => new
            {
                playerId = t.p.PlayerId,
                name = names.GetValueOrDefault(t.p.PlayerId),
                contributionBefore = t.old?.Contribution ?? 0,
                contributionAfter = t.p.Contribution,
                pointsBefore = t.old?.Points.Total ?? 0,
                pointsAfter = t.p.Points.Total,
                because = Because(t.old?.Points, t.p.Points),
            })
            .ToList();

        return new
        {
            teamId = team.Id,
            gameweek = info.ToRef(now),
            provisional = revision is null,
            revision,
            current.Total,
            current.PointsBeforeHits,
            current.TransferCost,
            current.BenchPoints,
            current.Chip,
            players = current.Picks.Select(p => new
            {
                p.PlayerId, name = names.GetValueOrDefault(p.PlayerId), p.IsStarter, p.BenchOrder, p.IsCaptain, p.IsViceCaptain,
                p.Multiplier, p.Counted, p.Contribution, p.Reason, points = p.Points.Total,
                fixtures = p.Fixtures.Select(f => new { f.FixtureId, items = Items(f.Points), total = f.Points.Total, f.Points.Minutes }),
            }),
            previousTotal = previous?.Total,
            changesSincePreviousRevision = changes,
            summary = previous is null
                ? $"{current.Total} points: {current.PointsBeforeHits} from players minus {current.TransferCost} for transfers."
                : $"Your score changed from {previous.Total} to {current.Total} in revision {revision!.Id} ({revision.Reason}).",
        };
    }

    private static IEnumerable<object> Items(PointsBreakdown b)
    {
        if (b.Appearance != 0) yield return new { label = $"Appearance ({b.Minutes} min)", points = b.Appearance };
        if (b.GoalPoints != 0) yield return new { label = $"{b.Goals} goal(s)", points = b.GoalPoints };
        if (b.AssistPoints != 0) yield return new { label = $"{b.Assists} assist(s)", points = b.AssistPoints };
        if (b.CleanSheetPoints != 0) yield return new { label = "Clean sheet", points = b.CleanSheetPoints };
        if (b.YellowPoints != 0) yield return new { label = $"{b.YellowCards} yellow card(s)", points = b.YellowPoints };
        if (b.RedPoints != 0) yield return new { label = "Red card", points = b.RedPoints };
        if (b.OwnGoalPoints != 0) yield return new { label = $"{b.OwnGoals} own goal(s)", points = b.OwnGoalPoints };
    }

    private static string Because(PointsBreakdown? before, PointsBreakdown after)
    {
        if (before is null) return "New in this revision.";
        var parts = new List<string>();
        if (before.Goals != after.Goals) parts.Add($"goals {before.Goals} → {after.Goals}");
        if (before.Assists != after.Assists) parts.Add($"assists {before.Assists} → {after.Assists}");
        if (before.Minutes != after.Minutes) parts.Add($"minutes {before.Minutes} → {after.Minutes}");
        if (before.CleanSheets != after.CleanSheets) parts.Add($"clean sheets {before.CleanSheets} → {after.CleanSheets}");
        if (before.YellowCards != after.YellowCards || before.RedCards != after.RedCards) parts.Add("cards changed");
        if (before.OwnGoals != after.OwnGoals) parts.Add($"own goals {before.OwnGoals} → {after.OwnGoals}");
        return parts.Count == 0 ? "Multiplier changed." : string.Join("; ", parts);
    }

    // ---- Reads -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RevisionView>> HistoryAsync(int gameweekId, CancellationToken ct)
    {
        if (!await ReadyAsync(ct)) return [];
        return (await db.Database.SqlQuery<RevisionRow>(RevisionSelect($"WHERE gameweek_id = {gameweekId} ORDER BY id")).ToListAsync(ct)).Select(r => r.View()).ToList();
    }

    public async Task<RevisionView?> PublishedRevisionAsync(int gameweekId, CancellationToken ct) =>
        await ReadyAsync(ct) && (await StoredStateAsync(gameweekId, ct))?.PublishedRevisionId is { } id ? await RevisionAsync(id, ct) : null;

    private async Task<TeamScoreBreakdown?> TeamBreakdownAsync(int revisionId, int teamId, CancellationToken ct)
    {
        var json = await db.Database.SqlQuery<string>($"""
            SELECT breakdown::text AS "Value" FROM app.score_revision_teams WHERE revision_id = {revisionId} AND fantasy_team_id = {teamId}
            """).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<TeamScoreBreakdown>(json, Json);
    }

    private Task<List<RevisionTeamRow>> RevisionTeamsAsync(int revisionId, CancellationToken ct) =>
        db.Database.SqlQuery<RevisionTeamRow>($"""
            SELECT fantasy_team_id AS "FantasyTeamId", total AS "Total", breakdown::text AS "Breakdown" FROM app.score_revision_teams WHERE revision_id = {revisionId}
            """).ToListAsync(ct);

    private async Task<RevisionView?> RevisionAsync(int id, CancellationToken ct) =>
        (await db.Database.SqlQuery<RevisionRow>(RevisionSelect($"WHERE id = {id}")).FirstOrDefaultAsync(ct))?.View();

    private static FormattableString RevisionSelect(FormattableString where) => FormattableStringFactory(
        """
        SELECT id AS "Id", gameweek_id AS "GameweekId", kind AS "Kind", status AS "Status", input_revision AS "InputRevision",
               previous_revision_id AS "PreviousRevisionId", rules_version AS "RulesVersion", source AS "Source", reason AS "Reason",
               correction_message AS "CorrectionMessage", author_user_id AS "AuthorUserId", created_at AS "CreatedAt", published_at AS "PublishedAt"
        FROM app.score_revisions
        """, where);

    private static FormattableString FormattableStringFactory(string select, FormattableString where) =>
        System.Runtime.CompilerServices.FormattableStringFactory.Create(select + " " + where.Format, where.GetArguments());

    private async Task<StateRow?> StoredStateAsync(int gameweekId, CancellationToken ct) =>
        await db.Database.SqlQuery<StateRow>($"""
            SELECT state AS "State", published_revision_id AS "PublishedRevisionId" FROM app.gameweek_settlement WHERE gameweek_id = {gameweekId}
            """).FirstOrDefaultAsync(ct);

    private async Task<Dictionary<int, ResolutionRow>> ResolutionsAsync(IReadOnlyCollection<int> fixtureIds, CancellationToken ct)
    {
        var ids = fixtureIds.ToArray();
        var rows = await db.Database.SqlQuery<ResolutionRow>($"""
            SELECT DISTINCT ON (fixture_id) fixture_id AS "FixtureId", outcome AS "Outcome", fixture_revision AS "FixtureRevision"
            FROM app.fixture_resolutions WHERE fixture_id = ANY({ids}) ORDER BY fixture_id, id DESC
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.FixtureId);
    }

    private async Task<Dictionary<int, int>> SeasonTotalsAsync(int seasonId, CancellationToken ct) =>
        await (from t in db.FantasyTeams
               where t.SeasonId == seasonId
               select new { t.Id, Total = db.GameweekEntries.Where(e => e.FantasyTeamId == t.Id && e.ScoredAt != null).Sum(e => (int?)e.Points) ?? 0 })
            .ToDictionaryAsync(x => x.Id, x => x.Total, ct);

    private async Task<(GameweekInfo Info, SeasonCalendar Calendar)> GameweekAsync(int gameweekId, CancellationToken ct)
    {
        var gameweek = await db.Gameweeks.FirstOrDefaultAsync(g => g.Id == gameweekId, ct) ?? throw new NotFoundException($"Gameweek {gameweekId} doesn't exist.");
        if (gameweek.SeasonId is not { } seasonId) throw new RuleViolationException("This gameweek has no season_id (V031 backfill).");
        var calendar = await gameweeks.GetCalendarAsync(seasonId, ct);
        return (calendar.ById(gameweekId)!, calendar);
    }

    private async Task RequireReadyAsync(CancellationToken ct)
    {
        if (!await ReadyAsync(ct)) throw new SchemaNotReadyException("Settlement needs docs/migrations/011_settlement.sql (touchline-sync V043).");
    }
}
