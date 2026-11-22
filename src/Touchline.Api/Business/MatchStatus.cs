namespace Touchline.Api.Business;

/// <summary>Simplified match status for API consumers.</summary>
public enum MatchStatus
{
    Scheduled,
    Live,
    Finished,
    NoResult,
}

public static class MatchStatusExtensions
{
    /// <summary>
    /// A result is final only at state 5, 7 or 8 (BACKEND_INTEGRATION.md §6).
    /// Never score a fixture that is not final.
    /// </summary>
    public static bool IsFinal(short? stateId) => stateId is
        SportmonksCodes.MatchStates.FullTime or
        SportmonksCodes.MatchStates.AfterExtraTime or
        SportmonksCodes.MatchStates.AfterPenalties;

    /// <summary>Same mapping as the Netlify fixtures function (statusFor).</summary>
    public static MatchStatus FromStateId(short? stateId)
    {
        if (IsFinal(stateId)) return MatchStatus.Finished;
        return stateId switch
        {
            SportmonksCodes.MatchStates.SecondHalf => MatchStatus.Live,
            SportmonksCodes.MatchStates.Postponed or
            SportmonksCodes.MatchStates.Cancelled or
            SportmonksCodes.MatchStates.Abandoned => MatchStatus.NoResult,
            _ => MatchStatus.Scheduled,
        };
    }
}
