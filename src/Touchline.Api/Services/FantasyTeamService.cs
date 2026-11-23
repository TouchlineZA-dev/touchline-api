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
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek);

        if (await squads.HasAnySquadAsync(team.Id, ct))
        {
            throw new ConflictException(
                "You've already picked your squad. Use PUT /api/v1/fantasy/team/lineup to rearrange it or POST /transfers to change players.",
                "Squad already picked");
        }

        var selection = request.ToSelection();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, selection.AllPlayers, ct);
        var check = _squadRules.ValidateSquad(selection, players, maxCost: _rules.Budget, requireAvailable: true);
        if (!check.IsValid) throw new RuleViolationException(check.Errors, "Your squad breaks some rules");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var picks = selection.AllPlayers.Select(id => new FantasySquadPick
        {
            FantasyTeamId = team.Id,
            PlayerId = id,
            GameweekId = gameweek.Id,
            PurchasePrice = players[id].Price,
        }).ToList();
        db.FantasySquads.AddRange(picks);
        await squads.ApplySelectionAsync(picks, selection, ct);
        db.GameweekEntries.Add(SquadStore.NewEntry(team.Id, gameweek.Id, selection.Formation));
        team.BudgetRemaining = _squadRules.BudgetAfterFirstPick(selection, players);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var saved = await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct);
        return await SquadViewAsync(ctx, team, gameweek, saved, check.Warnings, ct);
    }

    /// <summary>PUT /lineup: formation, starters, bench and captaincy for the next gameweek. Same 15 players.</summary>
    public async Task<SquadView> SaveLineupAsync(AppUser user, SquadRequest request, string? ifMatch, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: true, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
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
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek);
        var outIds = request.Out ?? [];
        var inIds = request.In ?? [];

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
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

    /// <summary>POST /chips for the next gameweek. Each chip once per season, one chip per gameweek.</summary>
    public async Task<SquadView> ActivateChipAsync(AppUser user, ChipRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.WritableGameweek(ctx, request.Gameweek);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var squad = await RequireSquadForWriteAsync(ctx, team, gameweek, ifMatch: null, ct);

        var used = await db.ChipUsage
            .Where(c => c.FantasyTeamId == team.Id && c.SeasonId == ctx.Season.Id)
            .Select(c => new ChipUse(c.Chip, c.GameweekId))
            .ToListAsync(ct);
        var check = ChipRules.CanActivate(request.Chip, used, gameweek.Id);
        if (!check.IsValid) throw new RuleViolationException(check.Errors, "That chip can't be played");

        db.ChipUsage.Add(new FantasyChipUsage { FantasyTeamId = team.Id, SeasonId = ctx.Season.Id, Chip = request.Chip, GameweekId = gameweek.Id });
        squad.Entry.Chip = request.Chip;
        if (request.Chip == Chips.Wildcard)
        {
            squad.Entry.TransferCost = 0; // Wildcard makes every transfer this gameweek free, including ones already made.
        }
        squad.Entry.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (FantasyContextService.IsUniqueViolation(ex))
        {
            throw new ConflictException("That chip was just played from another device.", "Chip already played");
        }
        await transaction.CommitAsync(ct);

        return await SquadViewAsync(ctx, team, gameweek, await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct), [], ct);
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

        var chipsUsed = await db.ChipUsage
            .Where(c => c.FantasyTeamId == team.Id && c.SeasonId == ctx.Season.Id)
            .Select(c => c.Chip)
            .ToListAsync(ct);

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
    private async Task<LoadedSquad> RequireSquadForWriteAsync(
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
    private async Task<bool> IsTeamsFirstGameweekAsync(int teamId, GameweekInfo gameweek, SeasonCalendar calendar, CancellationToken ct)
    {
        var earlier = calendar.Gameweeks.Where(g => g.Number < gameweek.Number).Select(g => g.Id).ToList();
        return !await db.FantasySquads.AnyAsync(p => p.FantasyTeamId == teamId && earlier.Contains(p.GameweekId), ct);
    }

    private static IEnumerable<int> PairIncoming(IReadOnlyList<int> outIds, IReadOnlyList<int> inIds, IReadOnlyDictionary<int, FantasyPlayer> players)
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

    private async Task<SquadView> SquadViewAsync(
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
