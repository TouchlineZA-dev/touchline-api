using Touchline.Api.Fantasy;

namespace Touchline.Api.Business;

/// <summary>Where a match minute comes from. The provider sends no live clock on our plan.</summary>
public enum ClockSource
{
    /// <summary>Not started, or won't be played: no minute.</summary>
    None,
    /// <summary>Estimated from the kickoff time and the server clock, never behind the latest recorded event.</summary>
    EstimatedFromKickoff,
    /// <summary>Half-time: the clock stands at 45.</summary>
    HalfTime,
    /// <summary>Clock paused (interrupted, a break, penalties): the latest recorded event minute.</summary>
    LatestEvent,
    /// <summary>Finished: the match length.</summary>
    Final,
}

/// <param name="Minute">Match minute (0 to the match length), or null when there is none.</param>
public sealed record MatchClockReading(int? Minute, ClockSource Source, bool Running);

/// <summary>
/// The one implementation of the match clock, shared by match detail, the fixture list and scoring
/// (scoring takes MatchEndMinute, which is the same number for live and finished matches).
/// </summary>
public sealed class MatchClock(FantasyRules rules)
{
    private readonly GameweekRules _gameweekRules = new(rules);

    public MatchClockReading Read(short? stateId, DateTimeOffset? kickoffUtc, short? length, int? latestEventMinute, DateTimeOffset now)
    {
        var state = MatchStateMatrix.For(stateId);
        var matchLength = length ?? rules.DefaultMatchLengthMinutes;

        if (state.ResultFinal && state.Played) return new(matchLength, ClockSource.Final, false);
        if (!state.Played) return new(null, ClockSource.None, false);
        if (state.Phase == MatchPhase.HalfTime) return new(45, ClockSource.HalfTime, false);
        if (!state.ClockRunning || kickoffUtc is null)
        {
            return new(Math.Clamp(latestEventMinute ?? 0, 0, matchLength), ClockSource.LatestEvent, false);
        }
        return new(_gameweekRules.LiveMinute(kickoffUtc.Value, now, length, latestEventMinute), ClockSource.EstimatedFromKickoff, true);
    }

    /// <summary>"45+2" for stoppage time, "67" otherwise.</summary>
    public static string Display(int? minute, int? extraMinute) =>
        minute is null ? "" : extraMinute is > 0 ? $"{minute}+{extraMinute}" : minute.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
