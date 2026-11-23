namespace Touchline.Api.Fantasy;

/// <summary>Free transfers and points hits for one gameweek (section 4).</summary>
public sealed class TransferRules(FantasyRules rules)
{
    /// <summary>Unlimited free transfers before the Gameweek 1 deadline (changes go to GW1) and while Wildcard is active.</summary>
    public static bool UnlimitedFreeTransfers(bool targetIsFirstGameweek, string? activeChip) =>
        targetIsFirstGameweek || activeChip == Chips.Wildcard;

    /// <param name="alreadyMade">Transfers already made for this gameweek.</param>
    /// <param name="newTransfers">Transfers in this request.</param>
    /// <param name="unlimited">True before the Gameweek 1 deadline or with Wildcard active.</param>
    public TransferCost Calculate(int alreadyMade, int newTransfers, bool unlimited)
    {
        var total = alreadyMade + newTransfers;
        if (unlimited)
        {
            return new TransferCost(total, GameweekCost: 0, RequestCost: 0, FreeTransfersLeft: null);
        }

        // Free transfers don't roll over, so each gameweek starts again at the allowance.
        int CostFor(int count) => Math.Max(0, count - rules.FreeTransfersPerGameweek) * rules.TransferHitPoints;

        var gameweekCost = CostFor(total);
        return new TransferCost(
            total,
            gameweekCost,
            RequestCost: gameweekCost - CostFor(alreadyMade),
            FreeTransfersLeft: Math.Max(0, rules.FreeTransfersPerGameweek - total));
    }
}

/// <param name="TransfersMade">Total transfers for the gameweek after this request.</param>
/// <param name="GameweekCost">Total points deducted for the gameweek (stored as transfer_cost).</param>
/// <param name="RequestCost">Points this request adds to the gameweek's cost.</param>
/// <param name="FreeTransfersLeft">Null means unlimited.</param>
public sealed record TransferCost(int TransfersMade, int GameweekCost, int RequestCost, int? FreeTransfersLeft);

/// <summary>A chip a team has played. GameweekId is the gameweek it applies to.</summary>
public sealed record ChipUse(string Chip, int GameweekId);

/// <summary>Each chip once per season, at most one chip per gameweek (section 5).</summary>
public static class ChipRules
{
    public static RuleCheck CanActivate(string? chip, IReadOnlyList<ChipUse> usedThisSeason, int gameweekId)
    {
        var check = new RuleCheck();
        if (!Chips.IsValid(chip))
        {
            return check.Fail($"'{chip}' isn't a chip. Choose one of {string.Join(", ", Chips.All)}.");
        }

        if (usedThisSeason.Any(u => u.Chip == chip))
        {
            check.Fail($"You've already used {DisplayName(chip!)} this season. Each chip can be used once per season.");
        }

        var activeThisWeek = usedThisSeason.FirstOrDefault(u => u.GameweekId == gameweekId);
        if (activeThisWeek is not null && activeThisWeek.Chip != chip)
        {
            check.Fail($"{DisplayName(activeThisWeek.Chip)} is already active this gameweek. Only one chip per gameweek.");
        }

        return check;
    }

    public static string DisplayName(string chip) => chip switch
    {
        Chips.BenchBoost => "Bench Boost",
        Chips.TripleCaptain => "Triple Captain",
        Chips.Wildcard => "Wildcard",
        _ => chip,
    };
}
