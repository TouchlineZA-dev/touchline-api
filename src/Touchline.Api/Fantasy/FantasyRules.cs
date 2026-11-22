using Touchline.Api.Business;

namespace Touchline.Api.Fantasy;

/// <summary>
/// Every fantasy rule number in one place. Bound from the "Fantasy" section of appsettings,
/// so a value can change without touching code. The defaults below are the decided rules.
/// Money is in millions of rand with one decimal: 7.5 = R7.5m.
/// </summary>
public sealed class FantasyRules
{
    public const string SectionName = "Fantasy";

    // ---- Teams and squad -------------------------------------------------------------

    /// <summary>Starting budget per team, in millions (R300.0m).</summary>
    public decimal Budget { get; set; } = 300.0m;

    /// <summary>Squad composition. 15 players: 2 GK, 5 DEF, 5 MID, 3 ATT.</summary>
    public int SquadGoalkeepers { get; set; } = 2;
    public int SquadDefenders { get; set; } = 5;
    public int SquadMidfielders { get; set; } = 5;
    public int SquadAttackers { get; set; } = 3;

    /// <summary>Most players allowed from one real club across the whole squad.</summary>
    public int MaxPlayersPerClub { get; set; } = 3;

    /// <summary>Allowed starting formations, DEF-MID-ATT, comma-separated (a string so config replaces it instead of appending).</summary>
    public string Formations { get; set; } = "4-3-3,4-4-2,3-5-2,3-4-3,5-3-2";

    /// <summary>Gameweek deadline: this many minutes before the first kickoff of the gameweek.</summary>
    public int DeadlineMinutesBeforeFirstKickoff { get; set; } = 90;

    // ---- Transfers and chips -----------------------------------------------------------

    /// <summary>Free transfers per gameweek. Unused ones do not roll over.</summary>
    public int FreeTransfersPerGameweek { get; set; } = 1;

    /// <summary>Points deducted for each transfer beyond the free ones (entered as a positive number).</summary>
    public int TransferHitPoints { get; set; } = 4;

    // ---- Team scoring ------------------------------------------------------------------

    public int CaptainMultiplier { get; set; } = 3;
    public int ViceCaptainMultiplier { get; set; } = 2;
    /// <summary>Captain multiplier with Triple Captain active (also applies to a vice-captain standing in).</summary>
    public int TripleCaptainMultiplier { get; set; } = 9;

    // ---- Player scoring (per fixture) ----------------------------------------------------

    /// <summary>Minutes needed for the appearance point.</summary>
    public int AppearanceMinMinutes { get; set; } = 45;
    /// <summary>Appearance points for 45+ minutes.</summary>
    public int AppearancePoints { get; set; } = 1;
    /// <summary>Appearance points in total for playing the full match (start to final whistle).</summary>
    public int FullMatchPoints { get; set; } = 2;

    public int GoalPoints { get; set; } = 3;
    public int AssistPoints { get; set; } = 2;
    /// <summary>Assists come from a goal event's related player, which is not always reliable.</summary>
    public bool AssistsEnabled { get; set; } = true;

    public int CleanSheetPoints { get; set; } = 2;
    public int CleanSheetMinMinutes { get; set; } = 60;

    public int YellowCardPoints { get; set; } = -1;
    /// <summary>Red card. A second yellow (type 21) counts as a red only, with no extra yellow deduction.</summary>
    public int RedCardPoints { get; set; } = -2;
    public int OwnGoalPoints { get; set; } = -2;

    /// <summary>Match length used when football.fixtures.length is null.</summary>
    public int DefaultMatchLengthMinutes { get; set; } = 90;
    /// <summary>Half-time break, used to turn the clock since kickoff into a match minute for live games.</summary>
    public int HalfTimeBreakMinutes { get; set; } = 15;

    /// <summary>How long provisional (live) points are cached in memory.</summary>
    public int LivePointsCacheSeconds { get; set; } = 60;

    // ---- Leagues -----------------------------------------------------------------------

    /// <summary>Most teams in one private league, owner included.</summary>
    public int LeagueMaxMembers { get; set; } = 20;
    public string JoinCodePrefix { get; set; } = "TL-";
    public int JoinCodeDigits { get; set; } = 6;
    /// <summary>Special characters placed between the digits (never two next to each other).</summary>
    public int JoinCodeSpecialCount { get; set; } = 2;
    public string JoinCodeSpecialChars { get; set; } = "#!@-";
    public int JoinCodeMaxAttempts { get; set; } = 20;

    // ---- Pricing -----------------------------------------------------------------------

    public PricingRules Pricing { get; set; } = new();

    // ---- Helpers (not settings) ----------------------------------------------------------

    public int SquadSize => SquadGoalkeepers + SquadDefenders + SquadMidfielders + SquadAttackers;

    public int SquadCount(Position position) => position switch
    {
        Position.GK => SquadGoalkeepers,
        Position.DEF => SquadDefenders,
        Position.MID => SquadMidfielders,
        Position.ATT => SquadAttackers,
        _ => 0,
    };

    public IReadOnlyList<Formation> AllowedFormations() =>
        Formations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => Formation.TryParse(f, out var parsed) ? parsed : throw new InvalidOperationException(
                $"Fantasy:Formations contains '{f}', which is not a DEF-MID-ATT formation like 4-4-2."))
            .ToList();
}

/// <summary>Player valuation settings (section 12). Fantasy:Pricing in appsettings.</summary>
public sealed class PricingRules
{
    /// <summary>Sportmonks competition used for price history: PSL league matches only, no cups.</summary>
    public long CompetitionSportmonksId { get; set; } = SportmonksCodes.PslCompetitionId;

    /// <summary>K in the small-sample safeguard: R_adj = (minutes × R + K × R_pos_avg) ÷ (minutes + K).</summary>
    public decimal ShrinkMinutes { get; set; } = 900m;

    /// <summary>Season weights for E: last season, two seasons ago, three seasons ago.</summary>
    public decimal WeightLastSeason { get; set; } = 0.6m;
    public decimal WeightTwoSeasonsAgo { get; set; } = 0.3m;
    public decimal WeightThreeSeasonsAgo { get; set; } = 0.1m;

    /// <summary>Mid-season runs: below this many minutes this season, M comes from last season instead.</summary>
    public int MidSeasonMinMinutes { get; set; } = 450;

    /// <summary>Prices are rounded to this step, halves up.</summary>
    public decimal PriceStep { get; set; } = 0.5m;

    public PriceRange Goalkeeper { get; set; } = new() { Min = 4.0m, Max = 6.0m };
    public PriceRange Defender { get; set; } = new() { Min = 4.0m, Max = 7.0m };
    public PriceRange Midfielder { get; set; } = new() { Min = 4.5m, Max = 12.0m };
    public PriceRange Attacker { get; set; } = new() { Min = 4.5m, Max = 12.5m };

    public IReadOnlyList<decimal> SeasonWeights() => [WeightLastSeason, WeightTwoSeasonsAgo, WeightThreeSeasonsAgo];

    public PriceRange RangeFor(Position position) => position switch
    {
        Position.GK => Goalkeeper,
        Position.DEF => Defender,
        Position.MID => Midfielder,
        Position.ATT => Attacker,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "No price range for an unknown position."),
    };
}

public sealed class PriceRange
{
    public decimal Min { get; set; }
    public decimal Max { get; set; }
}
