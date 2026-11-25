namespace Touchline.Api.Fantasy;

/// <summary>
/// The versioned season rulebook (docs/rules/rulebook-2026-27-v1.md), served read-only at
/// GET /api/v1/rules. Every number comes from the configured FantasyRules, so the published
/// rulebook is always the one the code applies. A rule change means a new version: entries and
/// scored results keep the version they were played and scored under (migration 008).
/// </summary>
public static class Rulebook
{
    public const string Version = "2026-27.v1";
    public const string PointsTableVersion = "2026-27.points.v1";
    public const string Season = "2026/2027";

    public static object Build(FantasyRules r) => new
    {
        version = Version,
        pointsTableVersion = PointsTableVersion,
        season = Season,
        document = "docs/rules/rulebook-2026-27-v1.md",
        money = new
        {
            unit = "millions of rand, one decimal (7.5 = R7.5m)",
            arithmetic = "decimal on the server, NUMERIC(5,1) in the database; never floating point",
            priceStep = r.Pricing.PriceStep,
            salePrice = "a player sells at his current price (no profit sharing); purchases keep the price paid",
            rounding = $"prices are set per transfer window to steps of {r.Pricing.PriceStep}, halves rounded up",
        },
        squad = new
        {
            budget = r.Budget,
            size = r.SquadSize,
            goalkeepers = r.SquadGoalkeepers,
            defenders = r.SquadDefenders,
            midfielders = r.SquadMidfielders,
            attackers = r.SquadAttackers,
            maxPerClub = r.MaxPlayersPerClub,
            formations = r.AllowedFormations().Select(f => f.ToString()).ToList(),
            startingGoalkeepers = 1,
            bench = "1 goalkeeper + 3 outfield players, in order; the bench goalkeeper only swaps with the goalkeeper",
            pickable = "players who have been in a current-season PSL matchday squad (lineup), unless an admin override says otherwise",
        },
        deadline = new
        {
            minutesBeforeFirstKickoff = r.DeadlineMinutesBeforeFirstKickoff,
            published = "every gameweek publishes its exact deadline instant in UTC and SAST (UTC+02:00, no daylight saving)",
            changes = "a future deadline can change only before it passes (an admin edit with a reason, e.g. a moved first kickoff); "
                      + "a passed deadline never changes, so published history is protected",
            notification = "managers see the new deadline in every gameweek response; push reminders arrive with notification preferences",
        },
        transfers = new
        {
            freePerGameweek = r.FreeTransfersPerGameweek,
            rollover = false,
            hitPoints = r.TransferHitPoints,
            likeForLike = true,
            unlimitedWhen = new[] { "before the Gameweek 1 deadline", "with Wildcard active", "before a late entrant's first deadline" },
        },
        captaincy = new
        {
            captainMultiplier = r.CaptainMultiplier,
            viceCaptainMultiplier = r.ViceCaptainMultiplier,
            viceTakesArmband = "only if the captain plays 0 minutes across ALL of the gameweek's fixtures",
            missingCaptain = "a missing captain or vice-captain is filled by a deterministic pick from the starting XI at scoring",
        },
        chips = new
        {
            available = Chips.All,
            tripleCaptainMultiplier = r.TripleCaptainMultiplier,
            tripleCaptain = "the captain scores ×4; passes to the vice-captain if the captain plays 0 minutes",
            benchBoost = "bench players' points count too",
            wildcard = "unlimited free transfers for the gameweek, including ones already made",
            limits = "each chip once per season, at most one chip per gameweek",
            periods = "one period: the whole season (no halves)",
            freeHit = false,
        },
        autoSubs = false,
        scoring = new
        {
            appearance45Plus = r.AppearancePoints,
            fullMatch = r.FullMatchPoints,
            appearanceMinMinutes = r.AppearanceMinMinutes,
            goal = r.GoalPoints,
            assist = r.AssistsEnabled ? r.AssistPoints : 0,
            assistSource = "the goal event's related player",
            cleanSheet = r.CleanSheetPoints,
            cleanSheetPositions = new[] { "GK", "DEF" },
            cleanSheetMinMinutes = r.CleanSheetMinMinutes,
            yellowCard = r.YellowCardPoints,
            redCard = r.RedCardPoints,
            secondYellow = $"{r.RedCardPoints} in total (counts as a red only)",
            ownGoal = r.OwnGoalPoints,
            notScored = new[] { "missed penalties", "saves", "defensive contributions", "bonus" },
            unattributed = "a goal or card with no identifiable player counts for the team score (and clean sheets) but gives nobody points",
        },
        minutes = new
        {
            counting = "inclusive: off at minute m plays m; on at minute m plays (length − m + 1)",
            stoppage = "45+x counts as 45 (a 45+x substitute plays from 46); 90+x counts as 90",
            cap = "never more than the match length (90 when unknown)",
            source = "starting XI, substitutions and red cards from the stored events",
        },
        doubleAndBlankWeeks = "a player scores in every fixture assigned to the gameweek; a gameweek with no fixture for his club scores 0 for him",
        lateEntry = "a team created after Gameweek 1 starts in the next gameweek whose deadline hasn't passed; it has unlimited free transfers "
                    + "before that first deadline and no points for earlier gameweeks",
        fixtureReassignment = "fixtures belong to the gameweek they are assigned to (persisted, with history); a postponed fixture leaves its gameweek "
                              + "and counts in the gameweek of its new kickoff; moving a fixture into or out of a locked gameweek needs a recorded admin decision",
        corrections = "late provider corrections are applied only by an admin re-score, which is audited and creates a new revision; "
                      + "past entries keep the rules version they were scored under",
        ties = new
        {
            leagues = "highest points first; ties go to overall points, then username (A–Z), then team id",
            overall = "same as leagues",
            psltable = "points, goal difference, goals for, then club name",
        },
        leagues = new
        {
            maxMembers = r.LeagueMaxMembers,
            joinBy = "exact code only",
            codeFormat = $"{r.JoinCodePrefix} + {r.JoinCodeDigits} digits with {r.JoinCodeSpecialCount} of '{r.JoinCodeSpecialChars}' between digits",
            ownerCanLeave = false,
            overall = true,
        },
        differencesFromFplClassic = Differences,
    };

    /// <summary>One row per capability in 03-Leruo-API Stage 2: how Touchline differs from FPL Classic 2026/27, and why.</summary>
    public static readonly IReadOnlyList<object> Differences =
    [
        new { capability = "Entry", fpl = "One entry plays in many leagues; team details editable", touchline = "One named team per manager per season, editable name and username, late entry starts at the next open deadline", why = "Same model; late-entry policy made explicit" },
        new { capability = "Selection", fpl = "15 players, £100m, 3 per club, legal formations, autopick", touchline = "15 players (2/5/5/3), R100.0m, 3 per club, 4-3-3, 4-4-2, 3-5-2, 3-4-3, 5-3-2 only", why = "PSL budget and a fixed formation list" },
        new { capability = "Deadline", fpl = "Before the gameweek deadline", touchline = "90 minutes before the gameweek's first kickoff, published in UTC and SAST", why = "Lineups arrive about 30 minutes before kickoff, after the deadline, on purpose" },
        new { capability = "Captain and bench", fpl = "Captain ×2, vice if captain plays 0, automatic substitutions", touchline = "Captain ×3, vice ×2 (×3 if the captain plays 0 across all the week's fixtures), no automatic substitutions", why = "Agreed Touchline rule; a starter who doesn't play scores 0" },
        new { capability = "Transfers", fpl = "1 free per week, bank up to 5, −4 per extra", touchline = "1 free per week, no rollover, −4 per extra; unlimited before GW1, with Wildcard and before a late entrant's first deadline", why = "Simpler allowance for launch" },
        new { capability = "Market prices", fpl = "Sale price shares profit; daily price changes", touchline = "Sell at the current price; prices change once per transfer window by a published formula", why = "Low PSL volume can't support demand-driven daily prices" },
        new { capability = "Chips", fpl = "Wildcard, Free Hit, Bench Boost, Triple Captain in each half", touchline = "Wildcard, Bench Boost, Triple Captain (×4), each once per season; no Free Hit", why = "Agreed scope for launch" },
        new { capability = "Scoring", fpl = "Position-specific goals/clean sheets, saves, penalties, defensive contributions, bonus", touchline = "Flat table: appearance, goal +3, assist +2, clean sheet (GK/DEF) +2, cards, own goal", why = "Only categories with reliable player-level data on our provider plan" },
        new { capability = "Rankings", fpl = "Live ranks, final scores, history", touchline = "Live provisional and final ranks from the same revision; ties by overall points then username", why = "Reproducible and explainable ranks" },
        new { capability = "Social competition", fpl = "Classic, general, head-to-head leagues and cups", touchline = "Private classic leagues by code (max 20) plus Overall", why = "Head-to-head and cups in later releases" },
        new { capability = "Player tools", fpl = "Watchlists, form, ownership, fixture difficulty, price indicators", touchline = "Minutes, fixture history, upcoming opponents and points reasons first", why = "Derive form and ownership once published data supports it" },
        new { capability = "Additional modes", fpl = "Draft, Challenge", touchline = "Season-long Classic only", why = "Each later mode needs its own entry, ownership and scoring contracts" },
    ];
}
