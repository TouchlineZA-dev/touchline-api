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
}
