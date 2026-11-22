namespace Touchline.Api.Business;

/// <summary>
/// Sportmonks numeric codes used in the football schema.
/// Source: BACKEND_INTEGRATION.md §6 (Touchline_Main and touchline-sync repos).
/// Full lists: GET https://api.sportmonks.com/v3/core/types and /v3/football/states.
/// </summary>
public static class SportmonksCodes
{
    /// <summary>Sportmonks league id for the South African Premier Division (PSL).</summary>
    public const long PslCompetitionId = 806;

    /// <summary>football.players.position_id / player_lineups.position_id.</summary>
    public static class PositionIds
    {
        public const short Goalkeeper = 24;
        public const short Defender = 25;
        public const short Midfielder = 26;
        public const short Attacker = 27;
    }

    /// <summary>football.match_events.type_id.</summary>
    public static class EventTypes
    {
        public const short Var = 10;
        public const short Goal = 14;
        public const short OwnGoal = 15;
        public const short PenaltyScored = 16;
        public const short PenaltyMissed = 17;
        public const short Substitution = 18;
        public const short YellowCard = 19;
        public const short RedCard = 20;
        public const short SecondYellowRed = 21;
    }

    /// <summary>football.fixtures.state_id.</summary>
    public static class MatchStates
    {
        public const short NotStarted = 1;
        public const short FullTime = 5;
        public const short AfterExtraTime = 7;
        public const short AfterPenalties = 8;
        public const short Postponed = 10;
        public const short Cancelled = 12;
        public const short Abandoned = 15;
        public const short AwaitingUpdates = 19;
        public const short SecondHalf = 22;
    }

    /// <summary>
    /// football.player_lineups.type_id. NOT YET CONFIRMED against the database, so both are null
    /// and minutes can't be worked out from the data yet (scoring and pricing return 503 until set).
    ///
    /// Lead to verify: Touchline_Main's matches.js treats 11 = starting XI and 12 = bench. Confirm with:
    ///   SELECT type_id, count(*), count(DISTINCT fixture_id) FROM football.player_lineups GROUP BY type_id;
    /// Expect about 11 starters per team per fixture for the starting-XI code.
    /// </summary>
    public static class LineupTypes
    {
        public static readonly short? StartingXi = null;
        public static readonly short? Bench = null;
    }

    /// <summary>
    /// Which player in a substitution event (type 18) is coming ON. NOT YET CONFIRMED against the database.
    ///
    /// Lead to verify: Touchline_Main's Pitch.tsx and MatchDetail.tsx treat sportmonks_player_id as the
    /// player coming on and sportmonks_related_player_id as the player going off. Confirm with:
    ///   SELECT e.minute, e.sportmonks_player_id, e.sportmonks_related_player_id, on_l.type_id AS player_lineup_type,
    ///          off_l.type_id AS related_lineup_type
    ///   FROM football.match_events e
    ///   LEFT JOIN football.player_lineups on_l  ON on_l.fixture_id = e.fixture_id AND on_l.sportmonks_player_id = e.sportmonks_player_id
    ///   LEFT JOIN football.player_lineups off_l ON off_l.fixture_id = e.fixture_id AND off_l.sportmonks_player_id = e.sportmonks_related_player_id
    ///   WHERE e.type_id = 18 LIMIT 50;
    /// The player coming on should be a bench player; the player going off a starter (or an earlier sub).
    /// </summary>
    public static class Substitutions
    {
        /// <summary>True: sportmonks_player_id is coming on. False: sportmonks_related_player_id is coming on. Null: unconfirmed.</summary>
        public static readonly bool? PlayerIdIsComingOn = null;
    }

    /// <summary>True once the lineup and substitution codes above have been confirmed and filled in.</summary>
    public static bool MinutesCodesConfirmed =>
        LineupTypes.StartingXi is not null && LineupTypes.Bench is not null && Substitutions.PlayerIdIsComingOn is not null;
}
