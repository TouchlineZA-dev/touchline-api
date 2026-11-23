using System.Globalization;
using Touchline.Api.Business;

namespace Touchline.Api.Fantasy;

/// <summary>
/// Squad, starting XI, bench, captaincy and transfer rules (sections 1, 2 and 4).
/// Pure: the caller supplies the players. Every check runs on every save and every
/// broken rule is reported, not just the first.
/// </summary>
public sealed class SquadRules(FantasyRules rules)
{
    /// <summary>
    /// Validates a full squad. <paramref name="maxCost"/> is the budget for a first squad
    /// pick; pass null when the budget is tracked through budget_remaining instead (transfers, lineup changes).
    /// <paramref name="requireAvailable"/> checks every player is pickable (first squad pick); players
    /// already in a squad are never blocked later.
    /// </summary>
    public RuleCheck ValidateSquad(
        SquadSelection selection, IReadOnlyDictionary<int, FantasyPlayer> players, decimal? maxCost, bool requireAvailable = false)
    {
        var check = new RuleCheck();
        var all = selection.AllPlayers.ToList();

        if (all.Count != rules.SquadSize)
        {
            check.Fail($"A squad needs exactly {rules.SquadSize} players; you have {all.Count}.");
        }

        foreach (var dup in all.GroupBy(id => id).Where(g => g.Count() > 1))
        {
            check.Fail($"{NameOf(dup.Key, players)} is in the squad more than once.");
        }

        var known = new List<FantasyPlayer>();
        foreach (var id in all.Distinct())
        {
            if (!players.TryGetValue(id, out var player))
            {
                check.Fail($"Player {id} doesn't exist.");
                continue;
            }
            if (player.Position == Position.Unknown)
            {
                check.Fail($"{player.Name} has no known position yet, so he can't be picked.");
                continue;
            }
            if (requireAvailable && !player.Available)
            {
                check.Fail(PlayerAvailability.NotAvailableMessage(player.Name));
            }
            known.Add(player);
        }

        foreach (var position in PositionNames.Playable)
        {
            var need = rules.SquadCount(position);
            var have = known.Count(p => p.Position == position);
            if (have != need)
            {
                check.Fail($"A squad needs exactly {need} {PositionNames.Plural(position, need)}; you have {have}.");
            }
        }

        foreach (var club in known.Where(p => p.ClubId is not null).GroupBy(p => p.ClubId))
        {
            var count = club.Count();
            if (count > rules.MaxPlayersPerClub)
            {
                var clubName = club.First().ClubName ?? $"club {club.Key}";
                check.Fail($"Max {rules.MaxPlayersPerClub} players from {clubName}; you have {count}.");
            }
        }

        foreach (var noClub in known.Where(p => p.ClubId is null))
        {
            check.Warn($"{noClub.Name} has no club yet this season, so he doesn't count towards any club limit.");
        }

        if (maxCost is not null)
        {
            var cost = known.Sum(p => p.Price);
            if (cost > maxCost.Value)
            {
                check.Fail($"The squad costs {Money(cost)}, which is over the {Money(maxCost.Value)} budget.");
            }
        }

        check.Merge(ValidateLineup(selection, players));
        return check;
    }

    /// <summary>Budget left after a first squad pick: the budget minus the players' current prices.</summary>
    public decimal BudgetAfterFirstPick(SquadSelection selection, IReadOnlyDictionary<int, FantasyPlayer> players) =>
        rules.Budget - selection.AllPlayers.Distinct().Sum(id => players[id].Price);

    /// <summary>
    /// A lineup change rearranges the same 15 players: formation, starters, bench and captaincy.
    /// Adding or removing players needs a transfer.
    /// </summary>
    public RuleCheck ValidateLineupChange(
        SquadSelection current, SquadSelection proposed, IReadOnlyDictionary<int, FantasyPlayer> players)
    {
        var check = new RuleCheck();
        var before = current.AllPlayers.ToHashSet();
        var after = proposed.AllPlayers.ToHashSet();

        if (!before.SetEquals(after) || proposed.AllPlayers.Count() != current.AllPlayers.Count())
        {
            check.Fail("A lineup change can't add or remove players; use transfers for that.");
        }

        var startingGoalkeepers = proposed.Starters.Count(id => PositionOf(id, players) == Position.GK);
        var benchGoalkeepers = proposed.Bench.Count(id => PositionOf(id, players) == Position.GK);
        if (startingGoalkeepers != 1 || benchGoalkeepers != 1)
        {
            check.Fail("The bench goalkeeper can only swap with the starting goalkeeper.");
        }

        return check.Merge(ValidateSquad(proposed, players, maxCost: null));
    }

    /// <summary>
    /// Validates transfers and, if they're valid, returns the new squad and budget.
    /// Each incoming player takes the outgoing player's place (starter or bench spot, and
    /// the armband if he had it). Selling returns the player's current price.
    /// </summary>
    public TransferOutcome ApplyTransfers(
        SquadSelection current,
        IReadOnlyList<int> outIds,
        IReadOnlyList<int> inIds,
        IReadOnlyDictionary<int, FantasyPlayer> players,
        decimal budgetRemaining)
    {
        var check = new RuleCheck();

        if (outIds.Count == 0 && inIds.Count == 0)
        {
            return new TransferOutcome(check.Fail("Choose at least one transfer."), null, budgetRemaining);
        }
        if (outIds.Count != inIds.Count)
        {
            check.Fail($"You're selling {outIds.Count} and buying {inIds.Count}; the numbers must match.");
        }

        foreach (var dup in outIds.GroupBy(id => id).Where(g => g.Count() > 1))
        {
            check.Fail($"{NameOf(dup.Key, players)} is listed to sell more than once.");
        }
        foreach (var dup in inIds.GroupBy(id => id).Where(g => g.Count() > 1))
        {
            check.Fail($"{NameOf(dup.Key, players)} is listed to buy more than once.");
        }

        var squad = current.AllPlayers.ToHashSet();
        foreach (var id in outIds.Distinct().Where(id => !squad.Contains(id)))
        {
            check.Fail($"{NameOf(id, players)} isn't in your squad.");
        }
        foreach (var id in inIds.Distinct())
        {
            if (squad.Contains(id) && !outIds.Contains(id))
            {
                check.Fail($"{NameOf(id, players)} is already in your squad.");
            }
            else if (!players.TryGetValue(id, out var p))
            {
                check.Fail($"Player {id} doesn't exist.");
            }
            else if (p.Position == Position.Unknown)
            {
                check.Fail($"{p.Name} has no known position yet, so he can't be picked.");
            }
            else if (!p.Available)
            {
                check.Fail(PlayerAvailability.NotAvailableMessage(p.Name));
            }
        }

        foreach (var position in PositionNames.Playable)
        {
            var selling = outIds.Count(id => PositionOf(id, players) == position);
            var buying = inIds.Count(id => PositionOf(id, players) == position);
            if (selling != buying)
            {
                check.Fail(
                    $"You're selling {selling} {PositionNames.Plural(position, selling)} but buying {buying}. " +
                    $"Transfers must swap like for like to keep {rules.SquadGoalkeepers} GK, {rules.SquadDefenders} DEF, " +
                    $"{rules.SquadMidfielders} MID, {rules.SquadAttackers} ATT.");
            }
        }

        if (!check.IsValid)
        {
            return new TransferOutcome(check, null, budgetRemaining);
        }

        // Pair each outgoing player with an incoming player of the same position, in request order.
        var replacements = new Dictionary<int, int>();
        var unmatchedIn = inIds.ToList();
        foreach (var outId in outIds)
        {
            var position = PositionOf(outId, players);
            var inId = unmatchedIn.First(id => PositionOf(id, players) == position);
            unmatchedIn.Remove(inId);
            replacements[outId] = inId;
        }

        int Swap(int id) => replacements.TryGetValue(id, out var replacement) ? replacement : id;
        int? SwapNullable(int? id) => id is null ? null : Swap(id.Value);

        var proposed = current with
        {
            Starters = current.Starters.Select(Swap).ToList(),
            Bench = current.Bench.Select(Swap).ToList(),
            CaptainId = SwapNullable(current.CaptainId),
            ViceCaptainId = SwapNullable(current.ViceCaptainId),
        };

        var newBudget = budgetRemaining
            + outIds.Sum(id => players[id].Price)
            - inIds.Sum(id => players[id].Price);
        if (newBudget < 0)
        {
            check.Fail($"These transfers cost {Money(-newBudget)} more than you have ({Money(budgetRemaining)} left).");
        }

        check.Merge(ValidateSquad(proposed, players, maxCost: null));
        return new TransferOutcome(check, check.IsValid ? proposed : null, newBudget);
    }

    /// <summary>Starting XI, bench and captaincy checks.</summary>
    private RuleCheck ValidateLineup(SquadSelection selection, IReadOnlyDictionary<int, FantasyPlayer> players)
    {
        var check = new RuleCheck();
        var allowed = rules.AllowedFormations();
        var startersNeeded = 1 + allowed.First().Outfield;
        var benchNeeded = rules.SquadSize - startersNeeded;

        if (!Formation.TryParse(selection.Formation, out var formation) || !allowed.Contains(formation))
        {
            check.Fail($"'{selection.Formation}' isn't an allowed formation. Choose one of {string.Join(", ", allowed)}.");
        }

        if (selection.Starters.Count != startersNeeded)
        {
            check.Fail($"The starting XI needs exactly {startersNeeded} players; you have {selection.Starters.Count}.");
        }

        var starting = selection.Starters.Select(id => PositionOf(id, players)).ToList();
        var gk = starting.Count(p => p == Position.GK);
        var def = starting.Count(p => p == Position.DEF);
        var mid = starting.Count(p => p == Position.MID);
        var att = starting.Count(p => p == Position.ATT);

        if (gk != 1)
        {
            check.Fail($"The starting XI needs exactly 1 goalkeeper; you have {gk}.");
        }

        RangeCheck(check, def, allowed.Min(f => f.Defenders), allowed.Max(f => f.Defenders), Position.DEF);
        RangeCheck(check, mid, allowed.Min(f => f.Midfielders), allowed.Max(f => f.Midfielders), Position.MID);
        RangeCheck(check, att, allowed.Min(f => f.Attackers), allowed.Max(f => f.Attackers), Position.ATT);

        var actual = new Formation(def, mid, att);
        if (allowed.Contains(formation) && actual != formation && gk + def + mid + att == selection.Starters.Count)
        {
            check.Fail(
                $"Your formation is {formation}, but your starting XI has {def} {PositionNames.Plural(Position.DEF, def)}, " +
                $"{mid} {PositionNames.Plural(Position.MID, mid)} and {att} {PositionNames.Plural(Position.ATT, att)}.");
        }

        if (selection.Bench.Count != benchNeeded)
        {
            check.Fail($"The bench needs exactly {benchNeeded} players; you have {selection.Bench.Count}.");
        }
        var benchGk = selection.Bench.Count(id => PositionOf(id, players) == Position.GK);
        if (benchGk != 1)
        {
            check.Fail($"The bench needs exactly 1 goalkeeper; you have {benchGk}.");
        }

        if (selection.CaptainId is null) check.Fail("Pick a captain.");
        if (selection.ViceCaptainId is null) check.Fail("Pick a vice-captain.");
        if (selection.CaptainId is not null && selection.CaptainId == selection.ViceCaptainId)
        {
            check.Fail("The captain and vice-captain must be different players.");
        }
        if (selection.CaptainId is { } c && !selection.Starters.Contains(c))
        {
            check.Fail("The captain must be in the starting XI.");
        }
        if (selection.ViceCaptainId is { } v && !selection.Starters.Contains(v))
        {
            check.Fail("The vice-captain must be in the starting XI.");
        }

        return check;
    }

    private static void RangeCheck(RuleCheck check, int have, int min, int max, Position position)
    {
        if (have < min || have > max)
        {
            var range = min == max ? $"{min}" : max - min == 1 ? $"{min} or {max}" : $"{min} to {max}";
            check.Fail($"The starting XI needs {range} {PositionNames.Plural(position, 2)}; you have {have}.");
        }
    }

    private static Position PositionOf(int id, IReadOnlyDictionary<int, FantasyPlayer> players) =>
        players.TryGetValue(id, out var p) ? p.Position : Position.Unknown;

    private static string NameOf(int id, IReadOnlyDictionary<int, FantasyPlayer> players) =>
        players.TryGetValue(id, out var p) ? p.Name : $"Player {id}";

    public static string Money(decimal millions) =>
        "R" + millions.ToString("0.0", CultureInfo.InvariantCulture) + "m";
}

public sealed record TransferOutcome(RuleCheck Check, SquadSelection? NewSelection, decimal NewBudgetRemaining);
