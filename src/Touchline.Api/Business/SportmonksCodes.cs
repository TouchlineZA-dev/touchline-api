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
    /// football.player_lineups.type_id. Confirmed against the database on 2026-11-22:
    /// type 11 averages 11.0 rows per team per fixture (starting XI), type 12 averages 9.1 (bench),
    /// over 294 fixtures.
    /// </summary>
    public static class LineupTypes
    {
        public static readonly short? StartingXi = 11;
        public static readonly short? Bench = 12;
    }

    /// <summary>
    /// Which player in a substitution event (type 18) is coming ON. Confirmed against the database on
    /// 2026-11-22: in 2,489 substitution events, sportmonks_player_id was a bench player (lineup type 12)
    /// 2,393 times, and sportmonks_related_player_id was a starter (type 11) 2,394 times. So
    /// sportmonks_player_id comes on and sportmonks_related_player_id goes off. The few exceptions
    /// fit a substitute who was later subbed off himself, or players missing from the lineup.
    /// </summary>
    public static class Substitutions
    {
        /// <summary>True: sportmonks_player_id is coming on and sportmonks_related_player_id going off.</summary>
        public static readonly bool? PlayerIdIsComingOn = true;
    }

    /// <summary>True once the lineup and substitution codes above have been confirmed and filled in.</summary>
    public static bool MinutesCodesConfirmed =>
        LineupTypes.StartingXi is not null && LineupTypes.Bench is not null && Substitutions.PlayerIdIsComingOn is not null;
}
