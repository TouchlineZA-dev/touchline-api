using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

// ---- Requests -------------------------------------------------------------------------------------

/// <summary>
/// A squad in progress. Send what the manager has so far: just <paramref name="Players"/> while picking the 15,
/// then starters, bench and armbands once arranging the team. Players in starters/bench needn't be repeated in Players.
/// </summary>
public sealed record SquadCheckRequest(
    IReadOnlyList<int>? Players = null, string? Formation = null, IReadOnlyList<int>? Starters = null, IReadOnlyList<int>? Bench = null,
    int? CaptainId = null, int? ViceCaptainId = null);

/// <summary>A proposed lineup for the saved squad (same 15 players), possibly unfinished.</summary>
public sealed record LineupCheckRequest(
    string? Formation = null, IReadOnlyList<int>? Starters = null, IReadOnlyList<int>? Bench = null, int? CaptainId = null, int? ViceCaptainId = null);

/// <summary>A transfer set in progress: Out and In may have different lengths while the manager is still choosing.</summary>
public sealed record TransferCheckRequest(IReadOnlyList<int>? Out = null, IReadOnlyList<int>? In = null);

// ---- Response -------------------------------------------------------------------------------------

public sealed record BudgetProgress(decimal Total, decimal Spent, decimal Left, bool OverBudget);

public sealed record PositionProgress(Position Position, int Have, int Need);

public sealed record ClubProgress(int ClubId, string? Club, int Count, int Max, bool OverLimit);

/// <param name="Requested">The formation the manager chose.</param>
/// <param name="Actual">What the starters add up to (DEF-MID-ATT), when there are 11 of them.</param>
public sealed record FormationProgress(string? Requested, string? Actual, bool Valid, IReadOnlyList<string> Allowed);

public sealed record LineupPartProgress(int StartersHave, int StartersNeed, int StartingGoalkeepers, int BenchHave, int BenchNeed, int BenchGoalkeepers, bool Valid);

public sealed record CaptaincyProgress(int? CaptainId, int? ViceCaptainId, bool Valid);

/// <param name="FreeTransfersBefore">Null = unlimited (GW1, Wildcard, a late entrant's first week).</param>
public sealed record TransferProgress(int Out, int In, bool Unlimited, int? FreeTransfersBefore, int? FreeTransfersAfter, int PointsHit, int GameweekPointsHit,
    decimal SaleProceeds, decimal PurchaseCost);

/// <summary>
/// Live progress for a wizard step. Never saves anything.
/// <paramref name="Complete"/>: everything the step needs has been given. <paramref name="Valid"/>: complete and no rule is broken,
/// so the matching save would be accepted (prices and the deadline permitting).
/// <paramref name="Violations"/>: rules already broken by what was sent (with rule names, docs/api/errors.md).
/// <paramref name="NextSteps"/>: what's still missing, in plain language ("Pick 2 more defenders").
/// </summary>
public sealed record WizardCheck(
    string Kind,
    bool Complete,
    bool Valid,
    BudgetProgress Budget,
    IReadOnlyList<PositionProgress> Positions,
    IReadOnlyList<ClubProgress> Clubs,
    FormationProgress? Formation,
    LineupPartProgress? Lineup,
    CaptaincyProgress? Captaincy,
    TransferProgress? Transfers,
    IReadOnlyList<RuleViolationItem> Violations,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextSteps,
    int? GameweekId,
    DateTimeOffset ServerTimeUtc);

/// <summary>
/// Step validation for wizards (docs/ui/wizard-contract.md): the same rule code as the saves
/// (Fantasy/SquadRules, TransferRules), applied to partial input, returning per-rule progress such as
/// "4/5 DEF, R12.5m left". Nothing is saved, and the result is never a promise: prices and deadlines are
/// checked again when the manager saves.
/// </summary>
public sealed class WizardService(
    FantasyContextService contexts,
    FantasyDataService data,
    SquadStore squads,
    FantasyTeamService teams,
    IOptions<FantasyRules> options)
{
    private readonly FantasyRules _rules = options.Value;
    private readonly SquadRules _squadRules = new(options.Value);
    private readonly TransferRules _transferRules = new(options.Value);

    // ---- Squad (first pick, onboarding) --------------------------------------------------------

    public async Task<WizardCheck> CheckSquadAsync(AppUser user, SquadCheckRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var starters = request.Starters ?? [];
        var bench = request.Bench ?? [];
        var arranged = starters.Concat(bench).ToList();
        // The squad is the arranged players plus any picked players not yet placed.
        var picked = arranged.Concat((request.Players ?? []).Where(id => !arranged.Contains(id))).ToList();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, picked, ct);

        var check = PartialSquadCheck(picked, players, _rules.Budget, requireAvailable: true, out var positions, out var clubs, out var cost);
        var lineupGiven = starters.Count > 0 || bench.Count > 0 || request.Formation is not null || request.CaptainId is not null || request.ViceCaptainId is not null;
        var lineupComplete = starters.Count == StartersNeeded && bench.Count == BenchNeeded && request.CaptainId is not null && request.ViceCaptainId is not null;
        var squadComplete = picked.Distinct().Count() == _rules.SquadSize && positions.All(p => p.Have == p.Need);

        var selection = new SquadSelection(request.Formation ?? "", starters, bench, request.CaptainId, request.ViceCaptainId);
        if (squadComplete && lineupComplete)
        {
            // Everything is there: the save's own check decides.
            check = _squadRules.ValidateSquad(selection, players, maxCost: _rules.Budget, requireAvailable: true);
        }
        else if (lineupGiven)
        {
            check.Merge(PartialLineupCheck(selection, players));
        }

        var next = new List<string>();
        foreach (var p in positions.Where(p => p.Have < p.Need))
        {
            next.Add($"Pick {p.Need - p.Have} more {PositionNames.Plural(p.Position, p.Need - p.Have)}.");
        }
        if (squadComplete) next.AddRange(LineupNextSteps(selection));

        return new WizardCheck("squad", squadComplete && lineupComplete, squadComplete && lineupComplete && check.IsValid,
            new BudgetProgress(_rules.Budget, cost, _rules.Budget - cost, cost > _rules.Budget),
            positions, clubs,
            lineupGiven ? FormationOf(selection, players) : null,
            lineupGiven ? LineupPartOf(selection, players) : null,
            lineupGiven ? CaptaincyOf(selection) : null,
            null, check.Violations, check.Warnings, next,
            ctx.Calendar.Next(ctx.Now)?.Id, ctx.Now);
    }

    // ---- Lineup and substitutions --------------------------------------------------------------

    public async Task<WizardCheck> CheckLineupAsync(AppUser user, LineupCheckRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.DefaultGameweek(ctx, null);
        var squad = await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct)
            ?? throw new NotFoundException("Pick your squad first with PUT /api/v1/fantasy/team/squad.");

        var current = squad.ToSelection();
        var proposed = new SquadSelection(request.Formation ?? "", request.Starters ?? [], request.Bench ?? [], request.CaptainId, request.ViceCaptainId);
        var players = await data.LoadPlayersAsync(ctx.Season.Id, current.AllPlayers.Concat(proposed.AllPlayers), ct);
        var complete = proposed.Starters.Count == StartersNeeded && proposed.Bench.Count == BenchNeeded
                       && proposed.CaptainId is not null && proposed.ViceCaptainId is not null;
        var check = complete ? _squadRules.ValidateLineupChange(current, proposed, players) : PartialLineupCheck(proposed, players);
        if (!complete)
        {
            // Even unfinished, every player must come from the saved squad.
            foreach (var id in proposed.AllPlayers.Distinct().Where(id => !current.AllPlayers.Contains(id)))
            {
                check.Fail("lineup_changes_players", $"{Name(id, players)} isn't in your squad; use transfers to bring him in.");
            }
        }

        var value = current.AllPlayers.Where(players.ContainsKey).Sum(id => players[id].Price);
        PartialSquadCheck(current.AllPlayers.ToList(), players, null, requireAvailable: false, out var positions, out var clubs, out _);
        return new WizardCheck("lineup", complete, complete && check.IsValid,
            new BudgetProgress(team.BudgetRemaining + value, value, team.BudgetRemaining, team.BudgetRemaining < 0),
            positions, clubs, FormationOf(proposed, players), LineupPartOf(proposed, players), CaptaincyOf(proposed), null,
            check.Violations, check.Warnings, LineupNextSteps(proposed).ToList(), gameweek.Id, ctx.Now);
    }

    // ---- Transfers ------------------------------------------------------------------------------

    public async Task<WizardCheck> CheckTransfersAsync(AppUser user, TransferCheckRequest request, CancellationToken ct)
    {
        var ctx = await contexts.LoadAsync(user, trackTeam: false, ct);
        var team = ctx.RequireTeam();
        var gameweek = FantasyContextService.DefaultGameweek(ctx, null);
        var squad = await squads.LoadAsync(team.Id, gameweek, ctx.Calendar, ct)
            ?? throw new NotFoundException("Pick your squad first with PUT /api/v1/fantasy/team/squad.");
        var outIds = request.Out ?? [];
        var inIds = request.In ?? [];

        var current = squad.ToSelection();
        var players = await data.LoadPlayersAsync(ctx.Season.Id, current.AllPlayers.Concat(inIds), ct);
        var sale = outIds.Where(players.ContainsKey).Sum(id => players[id].Price);
        var purchase = inIds.Where(players.ContainsKey).Sum(id => players[id].Price);
        var bankAfter = team.BudgetRemaining + sale - purchase;
        var complete = outIds.Count > 0 && outIds.Count == inIds.Count;

        RuleCheck check;
        List<int> after;
        if (complete)
        {
            var outcome = _squadRules.ApplyTransfers(current, outIds, inIds, players, team.BudgetRemaining);
            check = outcome.Check;
            after = (outcome.NewSelection?.AllPlayers ?? current.AllPlayers.Except(outIds).Concat(inIds)).ToList();
        }
        else
        {
            check = new RuleCheck();
            var squadIds = current.AllPlayers.ToHashSet();
            foreach (var id in outIds.Distinct().Where(id => !squadIds.Contains(id))) check.Fail("transfer_not_in_squad", $"{Name(id, players)} isn't in your squad.");
            foreach (var id in inIds.Distinct().Where(id => squadIds.Contains(id) && !outIds.Contains(id))) check.Fail("transfer_already_in_squad", $"{Name(id, players)} is already in your squad.");
            foreach (var id in inIds.Distinct().Where(id => players.TryGetValue(id, out var p) && !p.Available)) check.Fail("player_unavailable", PlayerAvailability.NotAvailableMessage(players[id].Name));
            foreach (var id in inIds.Distinct().Where(id => !players.ContainsKey(id))) check.Fail("unknown_player", $"Player {id} doesn't exist.");
            if (bankAfter < 0) check.Fail("insufficient_funds", $"These transfers cost {SquadRules.Money(-bankAfter)} more than you have ({SquadRules.Money(team.BudgetRemaining)} left).");
            after = current.AllPlayers.Where(id => !outIds.Contains(id)).Concat(inIds).ToList();
        }

        // Positions and clubs as the squad would stand (incomplete sets show the gaps, e.g. 4/5 DEF).
        var partial = PartialSquadCheck(after, players, null, requireAvailable: false, out var positions, out var clubs, out _);
        foreach (var v in partial.Violations.Where(v => v.Rule == "club_limit")) check.Fail(v.Rule, v.Message);

        var unlimited = TransferRules.UnlimitedFreeTransfers(gameweek.IsFirst, squad.Entry.Chip, await teams.IsTeamsFirstGameweekAsync(team.Id, gameweek, ctx.Calendar, ct));
        var cost = _transferRules.Calculate(squad.Entry.TransfersMade, Math.Max(outIds.Count, inIds.Count), unlimited);
        int? freeBefore = unlimited ? null : Math.Max(0, _rules.FreeTransfersPerGameweek - squad.Entry.TransfersMade);

        var next = new List<string>();
        if (outIds.Count == 0 && inIds.Count == 0) next.Add("Choose a player to sell.");
        foreach (var p in positions.Where(p => p.Have < p.Need)) next.Add($"Buy {p.Need - p.Have} {PositionNames.Plural(p.Position, p.Need - p.Have)}.");
        foreach (var p in positions.Where(p => p.Have > p.Need)) next.Add($"Sell {p.Have - p.Need} {PositionNames.Plural(p.Position, p.Have - p.Need)}.");

        var value = current.AllPlayers.Where(players.ContainsKey).Sum(id => players[id].Price);
        var total = team.BudgetRemaining + value;
        return new WizardCheck("transfers", complete, complete && check.IsValid,
            new BudgetProgress(total, total - bankAfter, bankAfter, bankAfter < 0),
            positions, clubs, null, null, null,
            new TransferProgress(outIds.Count, inIds.Count, unlimited, freeBefore, cost.FreeTransfersLeft, cost.RequestCost, cost.GameweekCost, sale, purchase),
            check.Violations, check.Warnings, next, gameweek.Id, ctx.Now);
    }

    // ---- Shared pieces --------------------------------------------------------------------------

    private int StartersNeeded => 1 + _rules.AllowedFormations().First().Outfield;
    private int BenchNeeded => _rules.SquadSize - StartersNeeded;

    /// <summary>The squad-level rules that can already be broken before the squad is complete.</summary>
    private RuleCheck PartialSquadCheck(
        IReadOnlyList<int> ids, IReadOnlyDictionary<int, FantasyPlayer> players, decimal? budget, bool requireAvailable,
        out List<PositionProgress> positions, out List<ClubProgress> clubs, out decimal cost)
    {
        var check = new RuleCheck();
        foreach (var dup in ids.GroupBy(id => id).Where(g => g.Count() > 1)) check.Fail("duplicate_player", $"{Name(dup.Key, players)} is in the squad more than once.");
        if (ids.Distinct().Count() > _rules.SquadSize) check.Fail("squad_size", $"A squad has {_rules.SquadSize} players; you have {ids.Distinct().Count()}.");

        var known = new List<FantasyPlayer>();
        foreach (var id in ids.Distinct())
        {
            if (!players.TryGetValue(id, out var p)) { check.Fail("unknown_player", $"Player {id} doesn't exist."); continue; }
            if (p.Position == Position.Unknown) { check.Fail("unknown_position", $"{p.Name} has no known position yet, so he can't be picked."); continue; }
            if (requireAvailable && !p.Available) check.Fail("player_unavailable", PlayerAvailability.NotAvailableMessage(p.Name));
            known.Add(p);
        }

        positions = PositionNames.Playable.Select(pos => new PositionProgress(pos, known.Count(p => p.Position == pos), _rules.SquadCount(pos))).ToList();
        foreach (var p in positions.Where(p => p.Have > p.Need))
        {
            check.Fail("position_count", $"A squad has {p.Need} {PositionNames.Plural(p.Position, p.Need)}; you have {p.Have}.");
        }

        clubs = known.Where(p => p.ClubId is not null).GroupBy(p => p.ClubId!.Value)
            .Select(g => new ClubProgress(g.Key, g.First().ClubName, g.Count(), _rules.MaxPlayersPerClub, g.Count() > _rules.MaxPlayersPerClub))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Club).ToList();
        foreach (var c in clubs.Where(c => c.OverLimit))
        {
            check.Fail("club_limit", $"Max {_rules.MaxPlayersPerClub} players from {c.Club ?? $"club {c.ClubId}"}; you have {c.Count}.");
        }

        cost = known.Sum(p => p.Price);
        if (budget is { } b && cost > b) check.Fail("over_budget", $"The squad costs {SquadRules.Money(cost)}, which is over the {SquadRules.Money(b)} budget.");
        return check;
    }

    /// <summary>Lineup rules that can be checked on an unfinished lineup (nothing about counts not yet reached).</summary>
    private RuleCheck PartialLineupCheck(SquadSelection s, IReadOnlyDictionary<int, FantasyPlayer> players)
    {
        var check = new RuleCheck();
        var allowed = _rules.AllowedFormations();
        if (!string.IsNullOrEmpty(s.Formation) && (!Formation.TryParse(s.Formation, out var f) || !allowed.Contains(f)))
        {
            check.Fail("formation_not_allowed", $"'{s.Formation}' isn't an allowed formation. Choose one of {string.Join(", ", allowed)}.");
        }
        int Count(IEnumerable<int> ids, Position p) => ids.Count(id => players.TryGetValue(id, out var x) && x.Position == p);
        if (s.Starters.Count > StartersNeeded) check.Fail("starters_count", $"The starting XI needs exactly {StartersNeeded} players; you have {s.Starters.Count}.");
        if (Count(s.Starters, Position.GK) > 1) check.Fail("starting_gk", $"The starting XI needs exactly 1 goalkeeper; you have {Count(s.Starters, Position.GK)}.");
        foreach (var pos in new[] { Position.DEF, Position.MID, Position.ATT })
        {
            var max = allowed.Max(x => x.Count(pos));
            if (Count(s.Starters, pos) > max) check.Fail("formation_range", $"The starting XI can have at most {max} {PositionNames.Plural(pos, max)}; you have {Count(s.Starters, pos)}.");
        }
        if (s.Bench.Count > BenchNeeded) check.Fail("bench_count", $"The bench needs exactly {BenchNeeded} players; you have {s.Bench.Count}.");
        if (Count(s.Bench, Position.GK) > 1) check.Fail("bench_gk", $"The bench needs exactly 1 goalkeeper; you have {Count(s.Bench, Position.GK)}.");
        foreach (var id in s.Starters.Intersect(s.Bench)) check.Fail("duplicate_player", $"{Name(id, players)} is both starting and on the bench.");
        if (s.CaptainId is not null && s.CaptainId == s.ViceCaptainId) check.Fail("captain_vice_same", "The captain and vice-captain must be different players.");
        if (s.CaptainId is { } c && !s.Starters.Contains(c)) check.Fail("captain_not_starter", "The captain must be in the starting XI.");
        if (s.ViceCaptainId is { } v && !s.Starters.Contains(v)) check.Fail("vice_captain_not_starter", "The vice-captain must be in the starting XI.");
        return check;
    }

    private FormationProgress FormationOf(SquadSelection s, IReadOnlyDictionary<int, FantasyPlayer> players)
    {
        var allowed = _rules.AllowedFormations();
        string? actual = null;
        var valid = false;
        if (s.Starters.Count == StartersNeeded)
        {
            int Count(Position p) => s.Starters.Count(id => players.TryGetValue(id, out var x) && x.Position == p);
            var f = new Formation(Count(Position.DEF), Count(Position.MID), Count(Position.ATT));
            actual = f.ToString();
            valid = Count(Position.GK) == 1 && allowed.Contains(f) && (string.IsNullOrEmpty(s.Formation) || s.Formation == actual);
        }
        return new FormationProgress(string.IsNullOrEmpty(s.Formation) ? null : s.Formation, actual, valid, allowed.Select(f => f.ToString()).ToList());
    }

    private LineupPartProgress LineupPartOf(SquadSelection s, IReadOnlyDictionary<int, FantasyPlayer> players)
    {
        int Gk(IEnumerable<int> ids) => ids.Count(id => players.TryGetValue(id, out var x) && x.Position == Position.GK);
        var valid = s.Starters.Count == StartersNeeded && Gk(s.Starters) == 1 && s.Bench.Count == BenchNeeded && Gk(s.Bench) == 1;
        return new LineupPartProgress(s.Starters.Count, StartersNeeded, Gk(s.Starters), s.Bench.Count, BenchNeeded, Gk(s.Bench), valid);
    }

    private static CaptaincyProgress CaptaincyOf(SquadSelection s) => new(s.CaptainId, s.ViceCaptainId,
        s.CaptainId is { } c && s.ViceCaptainId is { } v && c != v && s.Starters.Contains(c) && s.Starters.Contains(v));

    private IEnumerable<string> LineupNextSteps(SquadSelection s)
    {
        if (s.Starters.Count < StartersNeeded) yield return $"Choose {StartersNeeded - s.Starters.Count} more starters.";
        if (s.Bench.Count < BenchNeeded) yield return $"Put {BenchNeeded - s.Bench.Count} more players on the bench (1 goalkeeper).";
        if (string.IsNullOrEmpty(s.Formation)) yield return "Choose a formation.";
        if (s.CaptainId is null) yield return "Pick a captain.";
        if (s.ViceCaptainId is null) yield return "Pick a vice-captain.";
    }

    private static string Name(int id, IReadOnlyDictionary<int, FantasyPlayer> players) =>
        players.TryGetValue(id, out var p) ? p.Name : $"Player {id}";
}
