using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>A team's own actions: create, pick a squad, change the lineup, transfer, play chips.</summary>
public sealed class FantasyTeamService(
    TouchlineDbContext db,
    FantasyContextService contexts,
    SquadStore squads,
    FantasyDataService data,
    PointsService points,
    LeagueService leagues,
    SchemaFeatures schema,
    IOptions<FantasyRules> options)
{
    private readonly FantasyRules _rules = options.Value;
    private readonly SquadRules _squadRules = new(options.Value);
    private readonly TransferRules _transferRules = new(options.Value);

    public async Task<TeamView> GetTeamAsync(AppUser user, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        return await TeamViewAsync(ctx, ctx.RequireTeam(), ct);
    }

    public async Task<TeamView> CreateTeamAsync(AppUser user, CreateTeamRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        if (ctx.Team is not null)
        {
            throw new ConflictException("You already have a team for this season. Each user has one team per season.", "Team exists");
        }

        var name = ValidTeamName(request.Name);

        var team = new FantasyTeam { UserId = user.Id, SeasonId = ctx.Season.Id, Name = name, BudgetRemaining = _rules.Budget };
        db.FantasyTeams.Add(team);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (FantasyContextService.IsUniqueViolation(ex))
        {
            throw new ConflictException("You already have a team for this season. Each user has one team per season.", "Team exists");
        }
        return await TeamViewAsync(ctx, team, ct);
    }

    /// <summary>Team names: 3–30 characters after trimming; names may repeat (usernames tell managers apart).</summary>
    public static string ValidTeamName(string? input)
    {
        var name = (input ?? "").Trim();
        if (name.Length is < 3 or > 30) throw new RuleViolationException("Give your team a name of 3 to 30 characters.");
        return name;
    }

    /// <summary>PATCH /fantasy/team: the owner renames the team. Id, picks, memberships and ranks are untouched; history is recorded.</summary>
    public async Task<TeamView> RenameTeamAsync(AppUser user, RenameTeamRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: true, ct);
        var team = ctx.RequireTeam();
        var name = ValidTeamName(request.Name);
        if (name == team.Name) return await TeamViewAsync(ctx, team, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await schema.HasTableAsync("app", "fantasy_team_name_history", ct))
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO app.fantasy_team_name_history (fantasy_team_id, old_name, new_name, changed_by_user_id)
                VALUES ({team.Id}, {team.Name}, {name}, {user.Id})
                """, ct);
        }
        team.Name = name;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await TeamViewAsync(ctx, team, ct);
    }

    public async Task<SquadView> GetSquadAsync(AppUser user, int? gameweekNumber, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.DefaultGameweek(ctx, gameweekNumber);
        var squad = await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct);
        return await SquadViewAsync(ctx, team, gameweek, squad, [], ct);
    }

    /// <summary>PUT /squad: the first full squad pick. Afterwards, use lineup changes and transfers.</summary>
    public async Task<SquadView> SaveSquadAsync(AppUser user, SquadRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: true, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek, request.GameweekId, _rules.RequireTargetGameweek);

        var selection = request.ToSelection();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, selection.AllPlayers, ct);
        var check = _squadRules.ValidateSquad(selection, players, maxCost: _rules.Budget, requireAvailable: true);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FantasyContextService.LockTeamAsync(db, team, ct);
        if (await squads.HasAnySquadAsync(team.Id, ct))
        {
            throw new ConflictException(
                "You've already picked your squad. Use PUT /api/v1/fantasy/team/lineup to rearrange it or POST /transfers to change players.",
                "Squad already picked", code: ErrorCodes.SquadAlreadyPicked);
        }
        if (!check.IsValid) throw new RuleViolationException(check.Errors, "Your squad breaks some rules");
        var picks = selection.AllPlayers.Select(id => new FantasySquadPick
        {
            FantasyTeamId = team.Id,
            PlayerId = id,
            GameweekId = gameweek.Id,
            PurchasePrice = players[id].Price,
        }).ToList();
        try
        {
            db.FantasySquads.AddRange(picks);
            await squads.ApplySelectionAsync(picks, selection, ct);
            db.GameweekEntries.Add(SquadStore.NewEntry(team.Id, gameweek.Id, selection.Formation));
            team.BudgetRemaining = _squadRules.BudgetAfterFirstPick(selection, players);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (FantasyContextService.IsUniqueViolation(ex))
        {
            // Two first saves raced (two devices): the unique (team, gameweek, player) and (team, gameweek) indexes let one win.
            throw new ConflictException(
                "Your squad was just saved from another device, so this one wasn't. Reload to see it.",
                "Squad already picked", code: ErrorCodes.SquadAlreadyPicked);
        }

        var saved = await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct);
        return await SquadViewAsync(ctx, team, gameweek, saved, check.Warnings, ct);
    }

    /// <summary>PUT /lineup: formation, starters, bench and captaincy for the next gameweek. Same 15 players.</summary>
    public async Task<SquadView> SaveLineupAsync(AppUser user, SquadRequest request, string? ifMatch, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: true, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek, request.GameweekId, _rules.RequireTargetGameweek);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FantasyContextService.LockTeamAsync(db, team, ct);
        var squad = await RequireSquadForWriteAsync(ctx, team, gameweek, ifMatch, ct);

        var current = squad.ToSelection();
        var proposed = request.ToSelection();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, current.AllPlayers.Concat(proposed.AllPlayers), ct);
        var check = _squadRules.ValidateLineupChange(current, proposed, players);
        if (!check.IsValid) throw new RuleViolationException(check.Errors, "Your lineup breaks some rules");

        await squads.ApplySelectionAsync(squad.Picks, proposed, ct);
        squad.Entry.Formation = proposed.Formation;
        squad.Entry.Version++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), check.Warnings, ct);
    }

    /// <summary>POST /transfers for the next gameweek. Selling returns the player's current price.</summary>
    public async Task<TransferResult> MakeTransfersAsync(AppUser user, TransfersRequest request, string? ifMatch, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: true, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek, request.GameweekId, _rules.RequireTargetGameweek);
        var outIds = request.Out ?? [];
        var inIds = request.In ?? [];

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FantasyContextService.LockTeamAsync(db, team, ct);
        var squad = await RequireSquadForWriteAsync(ctx, team, gameweek, ifMatch, ct);

        var current = squad.ToSelection();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, current.AllPlayers.Concat(inIds), ct);
        var outcome = _squadRules.ApplyTransfers(current, outIds, inIds, players, team.BudgetRemaining);
        if (!outcome.Check.IsValid) throw new RuleViolationException(outcome.Check.Errors, "These transfers break some rules");

        // Unlimited free transfers before the Gameweek 1 deadline and with Wildcard active.
        var unlimited = TransferRules.UnlimitedFreeTransfers(gameweek.IsFirst, squad.Entry.Chip, await IsTeamsFirstGameweekAsync(team.Id, gameweek, ctx.Calendar, ct));
        var cost = _transferRules.Calculate(squad.Entry.TransfersMade, outIds.Count, unlimited);

        // Each incoming player takes over the outgoing player's row: same starter/bench spot and armband.
        var replacements = outIds.Zip(PairIncoming(outIds, inIds, players)).ToList();
        foreach (var (outId, inId) in replacements)
        {
            var row = squad.Picks.Single(p => p.PlayerId == outId);
            row.PlayerId = inId;
            row.PurchasePrice = players[inId].Price;
            db.Transfers.Add(new FantasyTransfer
            {
                FantasyTeamId = team.Id,
                GameweekId = gameweek.Id,
                PlayerOutId = outId,
                PlayerInId = inId,
                PriceOut = players[outId].Price,
                PriceIn = players[inId].Price,
            });
        }

        squad.Entry.TransfersMade = cost.TransfersMade;
        squad.Entry.TransferCost = cost.GameweekCost;
        squad.Entry.Version++;
        team.BudgetRemaining = outcome.NewBudgetRemaining;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var view = await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), outcome.Check.Warnings, ct);
        return new TransferResult(outIds.Count, cost.RequestCost, cost.GameweekCost, cost.FreeTransfersLeft, team.BudgetRemaining, view, ctx.Now);
    }

    /// <summary>
    /// POST /chips for the next gameweek. Each chip once per season, one chip per gameweek.
    /// Wildcard waives every hit already charged for that gameweek (recorded on the entry: transfer_cost 0).
    /// A cancelled chip (see CancelChipAsync) is available again; its row is reused, never deleted.
    /// </summary>
    public async Task<SquadView> ActivateChipAsync(AppUser user, ChipRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek, request.GameweekId, _rules.RequireTargetGameweek);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FantasyContextService.LockTeamAsync(db, team, ct);
        var squad = await RequireSquadForWriteAsync(ctx, team, gameweek, ifMatch: null, ct);

        var rows = await ChipRowsAsync(team.Id, ctx.Season.Id, ct);
        var check = ChipRules.CanActivate(request.Chip, rows.Where(r => !r.Cancelled).Select(r => new ChipUse(r.Chip, r.GameweekId)).ToList(), gameweek.Id);
        if (!check.IsValid) throw new RuleViolationException(check.Errors, "That chip can't be played");

        var sameChip = rows.FirstOrDefault(r => r.Cancelled && r.Chip == request.Chip);
        var sameWeek = rows.FirstOrDefault(r => r.Cancelled && r.GameweekId == gameweek.Id);
        try
        {
            if (sameChip is not null && (sameWeek is null || sameWeek.Id == sameChip.Id))
            {
                await db.Database.ExecuteSqlAsync($"UPDATE app.fantasy_chip_usage SET gameweek_id = {gameweek.Id}, cancelled_at = NULL WHERE id = {sameChip.Id}", ct);
            }
            else if (sameChip is null && sameWeek is not null)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE app.fantasy_chip_usage SET chip = {request.Chip}, cancelled_at = NULL WHERE id = {sameWeek.Id}", ct);
            }
            else if (sameChip is not null)
            {
                throw new ConflictException(
                    "A chip you cancelled earlier is still recorded against this gameweek, and this chip has its own cancelled record. Contact support.",
                    "Chip records clash", code: ErrorCodes.ChipNotCancellable);
            }
            else
            {
                db.ChipUsage.Add(new FantasyChipUsage { FantasyTeamId = team.Id, SeasonId = ctx.Season.Id, Chip = request.Chip, GameweekId = gameweek.Id });
            }

            squad.Entry.Chip = request.Chip;
            if (request.Chip == Chips.Wildcard)
            {
                squad.Entry.TransferCost = 0; // Wildcard makes every transfer this gameweek free, including ones already made.
            }
            squad.Entry.Version++;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (FantasyContextService.IsUniqueViolation(ex))
        {
            throw new ConflictException("That chip was just played from another device.", "Chip already played", code: ErrorCodes.StaleSquad);
        }
        await transaction.CommitAsync(ct);

        return await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), [], ct);
    }

    /// <summary>
    /// DELETE /chips/{chip}: cancel Bench Boost or Triple Captain before the deadline of the gameweek it's
    /// active in. Wildcard can't be cancelled once played (transfers may already rely on it). The chip
    /// becomes available again; the row is marked cancelled (needs migration 010), never deleted.
    /// </summary>
    public async Task<SquadView> CancelChipAsync(AppUser user, string chip, int? gameweekId, CancellationToken ct)
    {
        if (!await schema.HasColumnAsync("app", "fantasy_chip_usage", "cancelled_at", ct))
        {
            throw new SchemaNotReadyException("Cancelling a chip needs docs/migrations/010_safe_writes.sql (touchline-sync V042).");
        }
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, null, gameweekId, _rules.RequireTargetGameweek);
        if (chip == Chips.Wildcard)
        {
            throw new ConflictException("Wildcard can't be cancelled once played.", "Chip can't be cancelled", code: ErrorCodes.ChipNotCancellable);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FantasyContextService.LockTeamAsync(db, team, ct);
        var squad = await RequireSquadForWriteAsync(ctx, team, gameweek, ifMatch: null, ct);
        var row = (await ChipRowsAsync(team.Id, ctx.Season.Id, ct)).FirstOrDefault(r => !r.Cancelled && r.Chip == chip && r.GameweekId == gameweek.Id)
            ?? throw new ConflictException($"{ChipRules.DisplayName(chip)} isn't active for Gameweek {gameweek.Number}.", "Chip not active", code: ErrorCodes.ChipNotCancellable);

        await db.Database.ExecuteSqlAsync($"UPDATE app.fantasy_chip_usage SET cancelled_at = now() WHERE id = {row.Id}", ct);
        squad.Entry.Chip = null;
        squad.Entry.Version++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), [], ct);
    }

    /// <summary>GET /chips: each chip's entitlement this season and what can be done with it now.</summary>
    public async Task<object> ChipsAsync(AppUser user, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var next = ctx.Calendar.Next(ctx.Now);
        var rows = (await ChipRowsAsync(team.Id, ctx.Season.Id, ct)).Where(r => !r.Cancelled).ToList();
        var activeNext = next is null ? null : rows.FirstOrDefault(r => r.GameweekId == next.Id);

        return new
        {
            seasonId = ctx.Season.Id,
            nextGameweek = next?.ToRef(ctx.Now),
            policy = new
            {
                perSeason = 1,
                perGameweek = 1,
                cancellation = "Bench Boost and Triple Captain can be cancelled before the deadline of their gameweek; Wildcard can't be cancelled once played.",
                wildcardAndHits = "Wildcard waives every transfer hit already charged for its gameweek.",
                freeHit = "not available",
            },
            chips = Chips.All.Select(chip =>
            {
                var row = rows.FirstOrDefault(r => r.Chip == chip);
                var number = row is null ? (int?)null : ctx.Calendar.ById(row.GameweekId)?.Number;
                var status = row is null ? "available" : next is not null && row.GameweekId == next.Id ? "active" : "used";
                var canActivate = row is null && next is not null && activeNext is null;
                return new
                {
                    chip,
                    name = ChipRules.DisplayName(chip),
                    status,
                    gameweekId = row?.GameweekId,
                    gameweekNumber = number,
                    canActivate,
                    canCancel = status == "active" && chip != Chips.Wildcard,
                };
            }).ToList(),
            serverTimeUtc = ctx.Now,
        };
    }

    internal sealed record ChipRow(int Id, string Chip, int GameweekId, bool Cancelled);

    internal sealed class ChipRowData
    {
        public int Id { get; set; }
        public string Chip { get; set; } = "";
        public int GameweekId { get; set; }
        public bool Cancelled { get; set; }
    }

    /// <summary>The team's chip rows this season, with the cancelled flag (always false before migration 010).</summary>
    private async Task<IReadOnlyList<ChipRow>> ChipRowsAsync(int teamId, int seasonId, CancellationToken ct)
    {
        if (await schema.HasColumnAsync("app", "fantasy_chip_usage", "cancelled_at", ct))
        {
            var data = await db.Database.SqlQuery<ChipRowData>($"""
                SELECT id AS "Id", chip AS "Chip", gameweek_id AS "GameweekId", cancelled_at IS NOT NULL AS "Cancelled"
                FROM app.fantasy_chip_usage WHERE fantasy_team_id = {teamId} AND season_id = {seasonId}
                """).ToListAsync(ct);
            return data.Select(d => new ChipRow(d.Id, d.Chip, d.GameweekId, d.Cancelled)).ToList();
        }
        return await db.ChipUsage.Where(c => c.FantasyTeamId == teamId && c.SeasonId == seasonId)
            .Select(c => new ChipRow(c.Id, c.Chip, c.GameweekId, false)).ToListAsync(ct);
    }

    /// <summary>GET /points: defaults to the current gameweek (the latest whose deadline has passed).</summary>
    public async Task<TeamPointsView> GetPointsAsync(AppUser user, int? gameweekNumber, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = gameweekNumber is not null
            ? ctx.Calendar.ByNumber(gameweekNumber.Value) ?? throw new NotFoundException($"Gameweek {gameweekNumber} doesn't exist.")
            : ctx.Calendar.Current(ctx.Now) ?? throw new NotFoundException("No gameweek has started yet.");
        return await points.TeamPointsAsync(team, gameweek, ctx.Calendar, ctx.Now, ct);
    }

    /// <summary>GET /snapshot: everything the app needs to show the team offline, in one call.</summary>
    public async Task<SnapshotView> SnapshotAsync(AppUser user, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var teamView = await TeamViewAsync(ctx, team, ct);

        var gameweek = ctx.Calendar.Next(ctx.Now) ?? ctx.Calendar.Current(ctx.Now);
        SquadView? squad = null;
        if (gameweek is not null)
        {
            squad = await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), [], ct);
        }

        var chipsUsed = (await ChipRowsAsync(team.Id, ctx.Season.Id, ct)).Where(r => !r.Cancelled).Select(r => r.Chip).ToList();

        TeamPointsView? latestPoints = null;
        if (ctx.Calendar.Current(ctx.Now) is { } current)
        {
            try
            {
                latestPoints = await points.TeamPointsAsync(team, current, ctx.Calendar, ctx.Now, ct);
            }
            catch (Exception ex) when (ex is SportmonksCodesNotConfirmedException or NotFoundException)
            {
                // The snapshot still works without points (codes unconfirmed, or no squad that gameweek).
            }
        }

        var myLeagues = await leagues.MyLeaguesAsync(ctx, ct);
        return new SnapshotView(teamView, squad, chipsUsed, squad?.FreeTransfersLeft, gameweek?.Deadline, latestPoints, myLeagues, ctx.Now);
    }

    /// <summary>Squad rows for writing, after checking If-Match against the gameweek's version.</summary>
    internal async Task<LoadedSquad> RequireSquadForWriteAsync(
        FantasyContext ctx, FantasyTeam team, GameweekInfo gameweek, string? ifMatch, CancellationToken ct)
    {
        var squad = await squads.EnsureAsync(team.Id, gameweek, ctx.Calendar, ctx.Now, ct)
            ?? throw new NotFoundException("Pick your squad first with PUT /api/v1/fantasy/team/squad.");

        await FantasyContextService.CheckIfMatchAsync(ifMatch, squad.Entry.Version, async () =>
            await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), [], ct));
        return squad;
    }

    /// <summary>
    /// Late entry (rulebook §5): the team has no squad in any earlier gameweek, so this is the first
    /// gameweek it plays. Its transfers are free until that gameweek's deadline.
    /// </summary>
    internal async Task<bool> IsTeamsFirstGameweekAsync(int teamId, GameweekInfo gameweek, SeasonCalendar calendar, CancellationToken ct)
    {
        var earlier = calendar.Gameweeks.Where(g => g.Number < gameweek.Number).Select(g => g.Id).ToList();
        return !await db.FantasySquads.AnyAsync(p => p.FantasyTeamId == teamId && earlier.Contains(p.GameweekId), ct);
    }

    internal static IEnumerable<int> PairIncoming(IReadOnlyList<int> outIds, IReadOnlyList<int> inIds, IReadOnlyDictionary<int, FantasyPlayer> players)
    {
        // Same pairing as SquadRules.ApplyTransfers: each outgoing player gets the first unmatched incoming player of his position.
        var unmatched = inIds.ToList();
        foreach (var outId in outIds)
        {
            var match = unmatched.First(id => players[id].Position == players[outId].Position);
            unmatched.Remove(match);
            yield return match;
        }
    }

    private async Task<TeamView> TeamViewAsync(FantasyContext ctx, FantasyTeam team, CancellationToken ct)
    {
        var next = ctx.Calendar.Next(ctx.Now);
        decimal? squadValue = null;
        var gameweek = next ?? ctx.Calendar.Current(ctx.Now);
        if (gameweek is not null && await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct) is { } squad)
        {
            var players = await data.LoadPlayersAsync(ctx.Season.Id, squad.Picks.Select(p => p.PlayerId), ct);
            squadValue = squad.Picks.Sum(p => players.TryGetValue(p.PlayerId, out var pl) ? pl.Price : 0m);
        }
        return new TeamView(team.Id, team.Name, team.SeasonId, team.BudgetRemaining, squadValue, next?.ToRef(ctx.Now));
    }

    internal async Task<SquadView> SquadViewAsync(
        FantasyContext ctx, FantasyTeam team, GameweekInfo gameweek, LoadedSquad? squad, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        if (squad is null)
        {
            return new SquadView(team.Id, gameweek.ToRef(ctx.Now), null, null, null, null, 0, 0, null,
                team.BudgetRemaining, 0m, Version: 0, Saved: false, [], ["No squad picked yet."], ctx.Now);
        }

        var players = await data.LoadPlayersAsync(ctx.Season.Id, squad.Picks.Select(p => p.PlayerId), ct);
        var unlimited = TransferRules.UnlimitedFreeTransfers(gameweek.IsFirst, squad.Entry.Chip, await IsTeamsFirstGameweekAsync(team.Id, gameweek, ctx.Calendar, ct));
        int? freeLeft = unlimited ? null : Math.Max(0, _rules.FreeTransfersPerGameweek - squad.Entry.TransfersMade);

        var views = squad.Picks.Select(p =>
        {
            var player = players.GetValueOrDefault(p.PlayerId);
            return new SquadPlayerView(
                p.PlayerId,
                player?.Name ?? $"Player {p.PlayerId}",
                player?.Position ?? Business.Position.Unknown,
                player?.ClubId,
                player?.ClubName,
                player?.Price ?? 0m,
                p.PurchasePrice,
                !p.IsOnBench,
                p.BenchOrder,
                p.IsCaptain,
                p.IsViceCaptain,
                NoClubYet: player?.ClubId is null);
        })
        .OrderBy(v => v.IsStarter ? 0 : 1).ThenBy(v => v.BenchOrder ?? 0).ThenBy(v => v.Position)
        .ToList();

        var allWarnings = warnings.Concat(views.Where(v => v.NoClubYet).Select(v => $"{v.Name} has no club yet this season.")).Distinct().ToList();
        var selection = squad.ToSelection();

        return new SquadView(
            team.Id,
            gameweek.ToRef(ctx.Now),
            squad.Entry.Formation,
            selection.CaptainId,
            selection.ViceCaptainId,
            squad.Entry.Chip,
            squad.Entry.TransfersMade,
            squad.Entry.TransferCost,
            freeLeft,
            team.BudgetRemaining,
            views.Sum(v => v.Price),
            squad.Entry.Version,
            squad.Saved,
            views,
            allWarnings,
            ctx.Now);
    }
}
