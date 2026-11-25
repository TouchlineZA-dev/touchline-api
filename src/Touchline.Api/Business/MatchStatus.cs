namespace Touchline.Api.Business;

/// <summary>
/// Simplified match status for API consumers. Unknown means the provider sent a state this API
/// doesn't recognise: it is preserved (stateId) and flagged for review, never guessed.
/// </summary>
public enum MatchStatus
{
    Scheduled,
    Live,
    Finished,
    NoResult,
    Unknown,
}

/// <summary>The detailed phase of a match, one per Sportmonks state (see MatchStateMatrix).</summary>
public enum MatchPhase
{
    Scheduled,
    Delayed,
    ToBeAnnounced,
    FirstHalf,
    HalfTime,
    SecondHalf,
    Break,
    ExtraTime,
    ExtraTimeBreak,
    ExtraTimeSecondHalf,
    PenaltyBreak,
    Penalties,
    Interrupted,
    Suspended,
    AwaitingUpdates,
    Finished,
    FinishedAfterExtraTime,
    FinishedAfterPenalties,
    Awarded,
    WalkOver,
    Postponed,
    Cancelled,
    Abandoned,
    Deleted,
    Pending,
    Unknown,
}

/// <summary>
/// How one provider state is interpreted everywhere (fixture list, match detail, scoring, settlement).
/// </summary>
/// <param name="Status">The simplified status.</param>
/// <param name="Phase">The detailed phase.</param>
/// <param name="Played">The match was (or is being) played on the pitch: its lineups and events produce fantasy points.</param>
/// <param name="ResultFinal">The scoreline is final (BACKEND_INTEGRATION.md §6: states 5, 7, 8, plus awarded/walk-over results).</param>
/// <param name="Settled">Nothing more will happen to this fixture in its gameweek: final, awarded, or won't be played (postponed, cancelled, abandoned, deleted).</param>
/// <param name="ClockRunning">The match clock is running (in play).</param>
/// <param name="NeedsReview">An operator should look at it: an unknown code, or a state the data can't resolve on its own.</param>
public sealed record MatchState(
    short? StateId,
    string Code,
    MatchStatus Status,
    MatchPhase Phase,
    bool Played,
    bool ResultFinal,
    bool Settled,
    bool ClockRunning,
    bool NeedsReview);

/// <summary>
/// The single provider-state matrix (team decision 2026-11-23 and Phatu's status-mapping ticket).
/// Every Sportmonks v3 fixture state is mapped explicitly, from the published states list
/// (docs.sportmonks.com/v3/definitions/states). A code that isn't in the list is never silently
/// mapped to "scheduled": it comes back as MatchStatus.Unknown with NeedsReview = true and the raw
/// state id preserved.
/// </summary>
public static class MatchStateMatrix
{
    private static readonly IReadOnlyDictionary<short, MatchState> Known = new Dictionary<short, MatchState>
    {
        [1] = new(1, "NS", MatchStatus.Scheduled, MatchPhase.Scheduled, Played: false, ResultFinal: false, Settled: false, ClockRunning: false, NeedsReview: false),
        [2] = new(2, "INPLAY_1ST_HALF", MatchStatus.Live, MatchPhase.FirstHalf, true, false, false, true, false),
        [3] = new(3, "HT", MatchStatus.Live, MatchPhase.HalfTime, true, false, false, false, false),
        // State 4 (BREAK) isn't described by Sportmonks; most likely the break before extra time. Counted as live.
        [4] = new(4, "BREAK", MatchStatus.Live, MatchPhase.Break, true, false, false, false, false),
        [5] = new(5, "FT", MatchStatus.Finished, MatchPhase.Finished, true, true, true, false, false),
        [6] = new(6, "INPLAY_ET", MatchStatus.Live, MatchPhase.ExtraTime, true, false, false, true, false),
        [7] = new(7, "AET", MatchStatus.Finished, MatchPhase.FinishedAfterExtraTime, true, true, true, false, false),
        [8] = new(8, "FT_PEN", MatchStatus.Finished, MatchPhase.FinishedAfterPenalties, true, true, true, false, false),
        [9] = new(9, "INPLAY_PENALTIES", MatchStatus.Live, MatchPhase.Penalties, true, false, false, false, false),
        [10] = new(10, "POSTPONED", MatchStatus.NoResult, MatchPhase.Postponed, false, false, true, false, false),
        // Suspended: stopped, to be resumed later (possibly another day). Not settled until resumed or resolved.
        [11] = new(11, "SUSPENDED", MatchStatus.NoResult, MatchPhase.Suspended, false, false, false, false, true),
        [12] = new(12, "CANCELLED", MatchStatus.NoResult, MatchPhase.Cancelled, false, false, true, false, false),
        [13] = new(13, "TBA", MatchStatus.Scheduled, MatchPhase.ToBeAnnounced, false, false, false, false, false),
        // Walk-over and awarded: the result stands, but no match was played on the pitch, so no player points.
        [14] = new(14, "WO", MatchStatus.Finished, MatchPhase.WalkOver, false, true, true, false, false),
        [15] = new(15, "ABANDONED", MatchStatus.NoResult, MatchPhase.Abandoned, false, false, true, false, false),
        [16] = new(16, "DELAYED", MatchStatus.Scheduled, MatchPhase.Delayed, false, false, false, false, false),
        [17] = new(17, "AWARDED", MatchStatus.Finished, MatchPhase.Awarded, false, true, true, false, false),
        // Interrupted: play stopped during the match (weather, crowd). Still in progress; the clock is paused.
        [18] = new(18, "INTERRUPTED", MatchStatus.Live, MatchPhase.Interrupted, true, false, false, false, true),
        // Awaiting updates: the provider hasn't confirmed what happened. Kept as "scheduled" (the 2026-11-23 audit
        // decision) so nothing is scored from it, and flagged for an operator.
        [19] = new(19, "AWAITING_UPDATES", MatchStatus.Scheduled, MatchPhase.AwaitingUpdates, false, false, false, false, true),
        [20] = new(20, "DELETED", MatchStatus.NoResult, MatchPhase.Deleted, false, false, true, false, true),
        [21] = new(21, "EXTRA_TIME_BREAK", MatchStatus.Live, MatchPhase.ExtraTimeBreak, true, false, false, false, false),
        [22] = new(22, "INPLAY_2ND_HALF", MatchStatus.Live, MatchPhase.SecondHalf, true, false, false, true, false),
        [23] = new(23, "INPLAY_ET_2ND_HALF", MatchStatus.Live, MatchPhase.ExtraTimeSecondHalf, true, false, false, true, false),
        [25] = new(25, "PEN_BREAK", MatchStatus.Live, MatchPhase.PenaltyBreak, true, false, false, false, false),
        [26] = new(26, "PENDING", MatchStatus.Scheduled, MatchPhase.Pending, false, false, false, false, true),
    };

    /// <summary>Every state this API recognises, for documentation and tests.</summary>
    public static IReadOnlyCollection<MatchState> All => Known.Values.ToList();

    /// <summary>
    /// States in which a fixture SHOULD be live and therefore polled: in play 2, 6, 9, 22, 23; breaks
    /// 3, 4, 21, 25; interrupted or pending 11, 16, 18, 19, 26. The sync owner's list (touchline-sync V040,
    /// sync.live_fixture_health, from Sportmonks /v3/core/states). It decides freshness and stalled-poll
    /// checks only; it doesn't change Status or scoring (a suspended or delayed match still isn't scored).
    /// </summary>
    public static readonly IReadOnlySet<short> ShouldBeLiveStates = new HashSet<short> { 2, 3, 4, 6, 9, 11, 16, 18, 19, 21, 22, 23, 25, 26 };

    public static bool ShouldBeLive(short? stateId) => stateId is { } id && ShouldBeLiveStates.Contains(id);

    /// <summary>
    /// The interpretation of a provider state. A null state (no state synced yet) is a scheduled match.
    /// An unrecognised state is Unknown and needs review.
    /// </summary>
    public static MatchState For(short? stateId)
    {
        if (stateId is null)
        {
            return new MatchState(null, "NONE", MatchStatus.Scheduled, MatchPhase.Scheduled, false, false, false, false, false);
        }
        return Known.TryGetValue(stateId.Value, out var state)
            ? state
            : new MatchState(stateId, "UNKNOWN", MatchStatus.Unknown, MatchPhase.Unknown, false, false, false, false, NeedsReview: true);
    }
}

public static class MatchStatusExtensions
{
    /// <summary>
    /// The match was played and its result is final: state 5, 7 or 8 (BACKEND_INTEGRATION.md §6).
    /// Never finalise points from a fixture that is not final. Awarded and walk-over results are final
    /// too but weren't played (see MatchState.ResultFinal / Played).
    /// </summary>
    public static bool IsFinal(short? stateId) => MatchStateMatrix.For(stateId) is { ResultFinal: true, Played: true };

    /// <summary>The simplified status. Same values as the Netlify fixtures function for the states it knew.</summary>
    public static MatchStatus FromStateId(short? stateId) => MatchStateMatrix.For(stateId).Status;

    public static MatchPhase PhaseOf(short? stateId) => MatchStateMatrix.For(stateId).Phase;
}
