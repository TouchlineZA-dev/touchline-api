using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed record QuotePlayer(int PlayerId, string Name, Position Position, int? ClubId, decimal Price);

/// <summary>A transfer quote (03-Leruo-API Stage 6 example shape). Money in millions of rand.</summary>
/// <param name="FreeTransfersBefore">Null means unlimited (GW1, Wildcard, late entrant's first week).</param>
/// <param name="PointsHit">Points this request would add to the gameweek's hits.</param>
public sealed record TransferQuote(
    int GameweekId,
    int GameweekNumber,
    DateTimeOffset? Deadline,
    int SquadVersion,
    int PriceRevision,
    DateTimeOffset QuotedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<QuotePlayer> Out,
    IReadOnlyList<QuotePlayer> In,
    decimal SaleProceeds,
    decimal PurchaseCost,
    decimal BankBefore,
    decimal BankAfter,
    int? FreeTransfersBefore,
    int? FreeTransfersAfter,
    int PointsHit,
    int GameweekPointsHit,
    bool Valid,
    IReadOnlyList<string> Violations,
    DateTimeOffset ServerTimeUtc);

public sealed record TransferPreviewRequest(int? GameweekId, IReadOnlyList<int> Out, IReadOnlyList<int> In);

/// <summary>Confirms a quote. The versions, costs and quote time come from the quote; Idempotency-Key is a header.</summary>
public sealed record TransferConfirmRequest(
    int? GameweekId, IReadOnlyList<int> Out, IReadOnlyList<int> In, int SquadVersion, int PriceRevision, DateTimeOffset QuotedAtUtc,
    decimal? SaleProceeds = null, decimal? PurchaseCost = null);

public sealed record ReceiptTransfer(int PlayerOutId, int PlayerInId, decimal PriceOut, decimal PriceIn);

public sealed record TransferReceipt(
    int ReceiptId,
    string Operation,
    int GameweekId,
    int SquadVersionBefore,
    int SquadVersionAfter,
    IReadOnlyList<ReceiptTransfer> Transfers,
    decimal SaleProceeds,
    decimal PurchaseCost,
    decimal BankBefore,
    decimal BankAfter,
    int? FreeTransfersAfter,
    int PointsHit,
    int GameweekPointsHit,
    DateTimeOffset CreatedAtUtc,
    bool Replayed,
    SquadView? Squad);

public sealed record AutopickRequest(string? Formation = null);

public sealed record AutopickResult(bool Valid, string? Formation, IReadOnlyList<SquadPlayerView> Players, int? CaptainId, int? ViceCaptainId,
    decimal Cost, decimal BankAfter, IReadOnlyList<string> Explanation, DateTimeOffset ServerTimeUtc);

public sealed record LedgerRow(int GameweekId, int Number, DateTimeOffset? Deadline, bool Locked, int? Granted, int Used, int HitPoints, string? Chip, int Receipts);

internal sealed class ReceiptRow
{
    public int Id { get; set; }
    public string PayloadHash { get; set; } = "";
    public string Result { get; set; } = "";
}

/// <summary>
/// Deadline-safe manager writes (03-Leruo-API Stage 4, handbook Gate 6): transfer preview and
/// confirmation with versions and an idempotency key at one decision point, autopick preview, the
/// transfer allowance ledger, and the deadline lock with its reconciliation report.
/// </summary>
public sealed class SafeWriteService(
    TouchlineDbContext db,
    FantasyContextService contexts,
    FantasyTeamService teams,
    SquadStore squads,
    FantasyDataService data,
    FootballService football,
    GameweekService gameweeks,
    SchemaFeatures schema,
    IOptions<FantasyRules> options)
{
    public const string TransferConfirmOperation = "transfer_confirm";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly FantasyRules _rules = options.Value;
    private readonly SquadRules _squadRules = new(options.Value);
    private readonly TransferRules _transferRules = new(options.Value);

    // ---- Preview -----------------------------------------------------------------------------

    /// <summary>POST /transfers/preview: what the transfers would cost. Saves nothing.</summary>
    public async Task<TransferQuote> PreviewAsync(AppUser user, TransferPreviewRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, null, request.GameweekId, _rules.RequireTargetGameweek);
        var squad = await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct)
            ?? throw new NotFoundException("Pick your squad first with PUT /api/v1/fantasy/team/squad.");
        return await QuoteAsync(ctx, team, gameweek, squad, request.Out ?? [], request.In ?? [], ct);
    }

    private async Task<TransferQuote> QuoteAsync(
        FantasyContext ctx, FantasyTeam team, GameweekInfo gameweek, LoadedSquad squad, IReadOnlyList<int> outIds, IReadOnlyList<int> inIds, CancellationToken ct)
    {
        var current = squad.ToSelection();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, current.AllPlayers.Concat(inIds), ct);
        var outcome = _squadRules.ApplyTransfers(current, outIds, inIds, players, team.BudgetRemaining);
        var unlimited = TransferRules.UnlimitedFreeTransfers(gameweek.IsFirst, squad.Entry.Chip, await teams.IsTeamsFirstGameweekAsync(team.Id, gameweek, ctx.Calendar, ct));
        var cost = _transferRules.Calculate(squad.Entry.TransfersMade, outIds.Count, unlimited);
        int? freeBefore = unlimited ? null : Math.Max(0, _rules.FreeTransfersPerGameweek - squad.Entry.TransfersMade);

        QuotePlayer View(int id) => players.TryGetValue(id, out var p)
            ? new QuotePlayer(p.PlayerId, p.Name, p.Position, p.ClubId, p.Price)
            : new QuotePlayer(id, $"Player {id}", Position.Unknown, null, 0m);
        var sale = outIds.Where(players.ContainsKey).Sum(id => players[id].Price);
        var purchase = inIds.Where(players.ContainsKey).Sum(id => players[id].Price);
        var expires = ctx.Now.AddMinutes(_rules.TransferQuoteMinutes);
        if (gameweek.Deadline is { } deadline && deadline < expires) expires = deadline;

        return new TransferQuote(
            gameweek.Id, gameweek.Number, gameweek.Deadline, squad.Entry.Version, await PriceRevisionAsync(ctx.Season.Id, ct),
            ctx.Now, expires,
            outIds.Select(View).ToList(), inIds.Select(View).ToList(),
            sale, purchase, team.BudgetRemaining, team.BudgetRemaining + sale - purchase,
            freeBefore, cost.FreeTransfersLeft, cost.RequestCost, cost.GameweekCost,
            outcome.Check.IsValid, outcome.Check.Errors, ctx.Now);
    }

    /// <summary>
    /// The price revision a quote is based on: the newest price change of the season (prices only change
    /// when a transfer-window run or an admin override writes app.player_price_changes). 0 before any change.
    /// </summary>
    private async Task<int> PriceRevisionAsync(int seasonId, CancellationToken ct) =>
        await db.PlayerPriceChanges.Where(c => c.SeasonId == seasonId).MaxAsync(c => (int?)c.Id, ct) ?? 0;

    // ---- Confirm -----------------------------------------------------------------------------

    /// <summary>
    /// POST /transfers/confirm with an Idempotency-Key header. One decision point, in one transaction:
    /// ownership (the token's team), target gameweek still open, squad version unchanged, price revision
    /// and costs unchanged, quote not expired, rules still met. Then transfers, picks, bank, allowance,
    /// version and the receipt are written together. Retrying with the same key and body returns the
    /// original receipt; the same key with a different body is refused.
    /// </summary>
    public async Task<TransferReceipt> ConfirmAsync(AppUser user, TransferConfirmRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("app", "fantasy_receipts", ct))
        {
            throw new SchemaNotReadyException("Transfer confirmation needs docs/migrations/010_safe_writes.sql (touchline-sync V046).");
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new RuleViolationException(["Send an Idempotency-Key header (1–100 characters, e.g. a UUID) with every transfer confirmation."],
                "Idempotency-Key missing", ErrorCodes.IdempotencyKeyRequired);
        }
        var key = idempotencyKey.Trim();
        var hash = PayloadHash(request);

        if (await StoredReceiptAsync(user.Id, key, hash, ct) is { } replay) return replay;

        try
        {
            return await ConfirmOnceAsync(user, request, key, hash, ct);
        }
        catch (Exception ex) when (FantasyContextService.IsUniqueViolation(ex))
        {
            // Two requests with the same key raced; the first one's receipt is the answer for both.
            db.ChangeTracker.Clear();
            return await StoredReceiptAsync(user.Id, key, hash, ct) ?? throw new ConflictException(
                "Your team was changed by another request at the same moment, so nothing was saved. Reload and try again.",
                "Team changed", code: ErrorCodes.StaleSquad);
        }
    }

    private async Task<TransferReceipt> ConfirmOnceAsync(AppUser user, TransferConfirmRequest request, string key, string hash, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: true, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, null, request.GameweekId, requireTarget: true);
        var outIds = request.Out ?? [];
        var inIds = request.In ?? [];

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FantasyContextService.LockTeamAsync(db, team, ct);
        // A duplicate of this request (double tap, retry) that was waiting on the lock: the first one has
        // committed its receipt by now, so answer with that receipt instead of 409 stale_squad.
        if (await StoredReceiptAsync(user.Id, key, hash, ct) is { } replay) return replay;
        var squad = await squads.EnsureAsync(team.Id, gameweek, ctx.Calendar, ctx.Now, ct)
            ?? throw new NotFoundException("Pick your squad first with PUT /api/v1/fantasy/team/squad.");

        if (squad.Entry.Version != request.SquadVersion)
        {
            throw new ConflictException(
                $"Your team changed since this quote (version {request.SquadVersion}, now {squad.Entry.Version}), so nothing was saved.",
                "Team changed", await teams.SquadViewAsync(ctx, team, gameweek, squad, [], ct), ErrorCodes.StaleSquad);
        }

        var fresh = await QuoteAsync(ctx, team, gameweek, squad, outIds, inIds, ct);
        if (fresh.PriceRevision != request.PriceRevision
            || (request.SaleProceeds is { } sale && sale != fresh.SaleProceeds)
            || (request.PurchaseCost is { } purchase && purchase != fresh.PurchaseCost))
        {
            throw new ConflictException("Prices changed since this quote, so nothing was saved. 'current' is a new quote.",
                "Prices changed", fresh, ErrorCodes.PriceChanged);
        }
        if (ctx.Now > request.QuotedAtUtc.AddMinutes(_rules.TransferQuoteMinutes) || request.QuotedAtUtc > ctx.Now.AddMinutes(1))
        {
            throw new ConflictException($"This quote expired (quotes last {_rules.TransferQuoteMinutes} minutes). 'current' is a new quote.",
                "Quote expired", fresh, ErrorCodes.QuoteExpired);
        }
        if (!fresh.Valid)
        {
            var code = fresh.BankAfter < 0 ? ErrorCodes.InsufficientFunds : ErrorCodes.RuleViolation;
            throw new RuleViolationException(fresh.Violations, "These transfers break some rules", code);
        }

        var players = await data.LoadPlayersAsync(ctx.Season.Id, squad.Picks.Select(p => p.PlayerId).Concat(inIds), ct);
        var versionBefore = squad.Entry.Version;
        var receiptTransfers = new List<ReceiptTransfer>();
        foreach (var (outId, inId) in outIds.Zip(FantasyTeamService.PairIncoming(outIds, inIds, players)))
        {
            var row = squad.Picks.Single(p => p.PlayerId == outId);
            row.PlayerId = inId;
            row.PurchasePrice = players[inId].Price;
            db.Transfers.Add(new FantasyTransfer
            {
                FantasyTeamId = team.Id, GameweekId = gameweek.Id, PlayerOutId = outId, PlayerInId = inId,
                PriceOut = players[outId].Price, PriceIn = players[inId].Price,
            });
            receiptTransfers.Add(new ReceiptTransfer(outId, inId, players[outId].Price, players[inId].Price));
        }

        var bankBefore = team.BudgetRemaining;
        squad.Entry.TransfersMade += outIds.Count;
        squad.Entry.TransferCost = fresh.GameweekPointsHit;
        squad.Entry.Version++;
        team.BudgetRemaining = fresh.BankAfter;
        await db.SaveChangesAsync(ct);   // version is a concurrency token: a concurrent write makes this fail (409 stale_squad)

        if (await schema.HasColumnAsync("app", "fantasy_gameweek_entries", "free_transfers_granted", ct))
        {
            int? granted = fresh.FreeTransfersBefore is null ? null : _rules.FreeTransfersPerGameweek;
            await db.Database.ExecuteSqlAsync($"UPDATE app.fantasy_gameweek_entries SET free_transfers_granted = {granted} WHERE id = {squad.Entry.Id}", ct);
        }

        var receipt = new TransferReceipt(0, TransferConfirmOperation, gameweek.Id, versionBefore, squad.Entry.Version, receiptTransfers,
            fresh.SaleProceeds, fresh.PurchaseCost, bankBefore, team.BudgetRemaining, fresh.FreeTransfersAfter, fresh.PointsHit,
            fresh.GameweekPointsHit, ctx.Now, Replayed: false, Squad: null);
        var receiptId = await db.Database.SqlQuery<int>($"""
            INSERT INTO app.fantasy_receipts (fantasy_team_id, user_id, gameweek_id, operation, idempotency_key, payload_hash, squad_version, result)
            VALUES ({team.Id}, {user.Id}, {gameweek.Id}, {TransferConfirmOperation}, {key}, {hash}, {squad.Entry.Version},
                    CAST({JsonSerializer.Serialize(receipt, Json)} AS jsonb))
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        await transaction.CommitAsync(ct);

        var view = await teams.SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), [], ct);
        return receipt with { ReceiptId = receiptId.Single(), Squad = view };
    }

    private async Task<TransferReceipt?> StoredReceiptAsync(int userId, string key, string hash, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<ReceiptRow>($"""
            SELECT id AS "Id", payload_hash AS "PayloadHash", result::text AS "Result" FROM app.fantasy_receipts
            WHERE user_id = {userId} AND operation = {TransferConfirmOperation} AND idempotency_key = {key}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        if (row.PayloadHash != hash)
        {
            throw new ConflictException("This Idempotency-Key was already used for different transfers. Use a new key for a new request.",
                "Idempotency-Key reused", code: ErrorCodes.IdempotencyKeyReused);
        }
        var stored = JsonSerializer.Deserialize<TransferReceipt>(row.Result, Json)!;
        return stored with { ReceiptId = row.Id, Replayed = true };
    }

    /// <summary>SHA-256 of the confirmation's meaning (gameweek, transfers, versions), so a retry matches and a different request doesn't.</summary>
    public static string PayloadHash(TransferConfirmRequest r)
    {
        var canonical = $"{TransferConfirmOperation}|gw:{r.GameweekId}|out:{string.Join(',', r.Out ?? [])}|in:{string.Join(',', r.In ?? [])}|v:{r.SquadVersion}|p:{r.PriceRevision}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    // ---- Autopick ----------------------------------------------------------------------------

    /// <summary>
    /// POST /autopick/preview: a legal 15 within the budget and club cap, or an explanation of why the
    /// player pool can't make one. Greedy: the most expensive affordable available player for each slot,
    /// always leaving enough money for the cheapest players in the remaining slots. Saves nothing.
    /// </summary>
    public async Task<AutopickResult> AutopickAsync(AppUser user, AutopickRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var formationText = string.IsNullOrWhiteSpace(request.Formation) ? "4-4-2" : request.Formation.Trim();
        var explanation = new List<string>();
        if (!Formation.TryParse(formationText, out var formation) || !_rules.AllowedFormations().Contains(formation))
        {
            return new AutopickResult(false, null, [], null, null, 0, _rules.Budget,
                [$"'{formationText}' isn't an allowed formation ({_rules.Formations})."], ctx.Now);
        }

        var pool = (await football.GetPlayersAsync(ctx.Season.Id, ct))
            .Where(p => p.Available && p.Position != Position.Unknown && p.TeamId is not null)
            .ToList();
        var needed = PositionNames.Playable.ToDictionary(p => p, p => _rules.SquadCount(p));
        foreach (var (position, count) in needed)
        {
            var have = pool.Count(p => p.Position == position);
            if (have < count) explanation.Add($"Only {have} pickable {PositionNames.Plural(position, have)}; a squad needs {count}.");
        }
        if (explanation.Count > 0) return new AutopickResult(false, formationText, [], null, null, 0, _rules.Budget, explanation, ctx.Now);

        var minPrice = needed.Keys.ToDictionary(p => p, p => pool.Where(x => x.Position == p).Min(x => x.Price));
        var slots = needed.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();   // GK, GK, DEF×5, MID×5, ATT×3
        var chosen = new List<PlayerSummary>();
        var clubCount = new Dictionary<int, int>();
        var budget = _rules.Budget;
        for (var i = 0; i < slots.Count; i++)
        {
            var reserve = slots.Skip(i + 1).Sum(p => minPrice[p]);
            var pick = pool
                .Where(p => p.Position == slots[i] && !chosen.Contains(p) && clubCount.GetValueOrDefault(p.TeamId!.Value) < _rules.MaxPlayersPerClub
                            && p.Price <= budget - reserve)
                .OrderByDescending(p => p.Price).ThenBy(p => p.Id)
                .FirstOrDefault();
            if (pick is null)
            {
                return new AutopickResult(false, formationText, [], null, null, 0, _rules.Budget,
                    [$"No affordable {slots[i]} left within R{budget:0.0}m while keeping R{reserve:0.0}m for the remaining slots and at most {_rules.MaxPlayersPerClub} per club."],
                    ctx.Now);
            }
            chosen.Add(pick);
            budget -= pick.Price;
            clubCount[pick.TeamId!.Value] = clubCount.GetValueOrDefault(pick.TeamId!.Value) + 1;
        }

        List<PlayerSummary> Top(Position p, int n) => chosen.Where(x => x.Position == p).OrderByDescending(x => x.Price).ThenBy(x => x.Id).Take(n).ToList();
        var starters = Top(Position.GK, 1).Concat(Top(Position.DEF, formation.Defenders)).Concat(Top(Position.MID, formation.Midfielders)).Concat(Top(Position.ATT, formation.Attackers)).ToList();
        var bench = chosen.Except(starters).OrderBy(x => x.Position == Position.GK ? 0 : 1).ThenByDescending(x => x.Price).ToList();
        var byPrice = starters.Where(x => x.Position != Position.GK).OrderByDescending(x => x.Price).ThenBy(x => x.Id).ToList();
        var selection = new SquadSelection(formationText, starters.Select(x => x.Id).ToList(), bench.Select(x => x.Id).ToList(), byPrice[0].Id, byPrice[1].Id);

        var players = chosen.ToDictionary(x => x.Id, x => new FantasyPlayer(x.Id, x.Name, x.Position, x.TeamId, x.Team, x.Price, x.Available));
        var check = _squadRules.ValidateSquad(selection, players, maxCost: _rules.Budget, requireAvailable: true);
        var views = starters.Select(x => (x, starter: true, order: (int?)null)).Concat(bench.Select((x, i) => (x, starter: false, order: (int?)(i + 1))))
            .Select(v => new SquadPlayerView(v.x.Id, v.x.Name, v.x.Position, v.x.TeamId, v.x.Team, v.x.Price, null, v.starter, v.order,
                v.x.Id == selection.CaptainId, v.x.Id == selection.ViceCaptainId, NoClubYet: false))
            .ToList();
        var cost = chosen.Sum(x => x.Price);
        return new AutopickResult(check.IsValid, formationText, views, selection.CaptainId, selection.ViceCaptainId, cost, _rules.Budget - cost,
            check.IsValid ? ["A legal squad within the budget. Review it, then save it with PUT /api/v1/fantasy/team/squad."] : check.Errors, ctx.Now);
    }

    // ---- Ledger --------------------------------------------------------------------------------

    /// <summary>GET /transfers/ledger: free transfers granted and used, and hits charged, per gameweek. No rollover.</summary>
    public async Task<object> LedgerAsync(AppUser user, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var entries = await db.GameweekEntries.Where(e => e.FantasyTeamId == team.Id).ToListAsync(ct);
        var receipts = await schema.HasTableAsync("app", "fantasy_receipts", ct)
            ? (await db.Database.SqlQuery<int>($"SELECT gameweek_id AS \"Value\" FROM app.fantasy_receipts WHERE fantasy_team_id = {team.Id}").ToListAsync(ct))
                .GroupBy(g => g).ToDictionary(g => g.Key, g => g.Count())
            : [];

        var rows = new List<LedgerRow>();
        foreach (var gw in ctx.Calendar.Gameweeks)
        {
            var entry = entries.FirstOrDefault(e => e.GameweekId == gw.Id);
            if (entry is null) continue;
            var unlimited = TransferRules.UnlimitedFreeTransfers(gw.IsFirst, entry.Chip, await teams.IsTeamsFirstGameweekAsync(team.Id, gw, ctx.Calendar, ct));
            rows.Add(new LedgerRow(gw.Id, gw.Number, gw.Deadline, gw.IsLocked(ctx.Now), unlimited ? null : _rules.FreeTransfersPerGameweek,
                entry.TransfersMade, entry.TransferCost, entry.Chip, receipts.GetValueOrDefault(gw.Id)));
        }
        return new { teamId = team.Id, rollover = false, freePerGameweek = _rules.FreeTransfersPerGameweek, hitPoints = _rules.TransferHitPoints, gameweeks = rows, serverTimeUtc = ctx.Now };
    }

    // ---- Lock and reconcile --------------------------------------------------------------------

    /// <summary>
    /// Admin: freeze every team's entry for a gameweek whose deadline has passed, then reconcile the
    /// accepted receipts with the locked entries (handbook "Matchday operations": at the deadline,
    /// reconcile accepted action receipts with locked entries).
    /// </summary>
    public async Task<object> LockReportAsync(int gameweekId, DateTimeOffset now, CancellationToken ct)
    {
        var gameweek = await db.Gameweeks.FirstOrDefaultAsync(g => g.Id == gameweekId, ct) ?? throw new NotFoundException($"Gameweek {gameweekId} doesn't exist.");
        var calendar = await gameweeks.GetCalendarAsync(gameweek.SeasonId ?? 0, ct);
        var info = calendar.ById(gameweekId) ?? throw new NotFoundException($"Gameweek {gameweekId} has no season.");
        if (!info.IsLocked(now)) throw new ConflictException($"Gameweek {info.Number}'s deadline hasn't passed yet.", "Not locked", code: ErrorCodes.GameweekLocked);

        var teamIds = await db.FantasyTeams.Where(t => t.SeasonId == gameweek.SeasonId).Select(t => t.Id).ToListAsync(ct);
        var locked = 0;
        foreach (var teamId in teamIds)
        {
            if (await squads.LockAsync(teamId, info, calendar, now, ct) is not null) locked++;
        }

        var transfers = await db.Transfers.Where(t => t.GameweekId == gameweekId).GroupBy(t => t.FantasyTeamId)
            .Select(g => new { Team = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Team, x => x.Count, ct);
        var receiptCounts = await schema.HasTableAsync("app", "fantasy_receipts", ct)
            ? (await db.Database.SqlQuery<int>($"SELECT fantasy_team_id AS \"Value\" FROM app.fantasy_receipts WHERE gameweek_id = {gameweekId}").ToListAsync(ct))
                .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count())
            : [];
        var entries = await db.GameweekEntries.Where(e => e.GameweekId == gameweekId).ToListAsync(ct);

        var mismatches = entries
            .Where(e => transfers.GetValueOrDefault(e.FantasyTeamId) != e.TransfersMade)
            .Select(e => new
            {
                fantasyTeamId = e.FantasyTeamId,
                transfersOnEntry = e.TransfersMade,
                transferRows = transfers.GetValueOrDefault(e.FantasyTeamId),
                receipts = receiptCounts.GetValueOrDefault(e.FantasyTeamId),
            })
            .ToList();

        return new
        {
            gameweekId,
            gameweek = info.Number,
            deadline = info.Deadline,
            teams = teamIds.Count,
            entriesLocked = locked,
            receipts = receiptCounts.Values.Sum(),
            transferRows = transfers.Values.Sum(),
            mismatches,
            reconciled = mismatches.Count == 0,
            generatedAtUtc = now,
        };
    }
}
