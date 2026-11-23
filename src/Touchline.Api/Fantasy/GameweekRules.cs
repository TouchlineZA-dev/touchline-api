using Touchline.Api.Business;

namespace Touchline.Api.Fantasy;

/// <summary>Gameweek timing: which fixtures belong, deadlines, and the live match minute (sections 2, 6 and 9).</summary>
public sealed class GameweekRules(FantasyRules rules)
{
    /// <summary>South Africa Standard Time. No daylight saving.</summary>
    public static readonly TimeSpan Sast = TimeSpan.FromHours(2);

    /// <summary>A fixture belongs to a gameweek when its kickoff, in SAST, falls on start_date..end_date inclusive.</summary>
    public static bool FixtureBelongs(DateTimeOffset kickoffUtc, DateOnly startDate, DateOnly endDate)
    {
        var sastDate = DateOnly.FromDateTime(kickoffUtc.ToOffset(Sast).DateTime);
        return sastDate >= startDate && sastDate <= endDate;
    }

    /// <summary>The deadline is a set time before the gameweek's first kickoff. Null when it has no fixtures yet.</summary>
    public DateTimeOffset? Deadline(IEnumerable<DateTimeOffset?> kickoffsUtc)
    {
        var first = kickoffsUtc.Where(k => k is not null).Min();
        return first?.AddMinutes(-rules.DeadlineMinutesBeforeFirstKickoff);
    }

    public static bool IsLocked(DateTimeOffset? deadline, DateTimeOffset now) => deadline is not null && now >= deadline;

    /// <summary>
    /// The current minute of a live match, from the clock: time since kickoff, minus the half-time
    /// break once past 45, capped at the match length, and never behind the latest recorded event.
    /// </summary>
    public int LiveMinute(DateTimeOffset kickoffUtc, DateTimeOffset now, int? length, int? latestEventMinute)
    {
        var matchLength = length ?? rules.DefaultMatchLengthMinutes;
        var elapsed = (int)Math.Floor((now - kickoffUtc).TotalMinutes);

        int minute;
        if (elapsed <= 45) minute = elapsed;
        else if (elapsed <= 45 + rules.HalfTimeBreakMinutes) minute = 45;
        else minute = elapsed - rules.HalfTimeBreakMinutes;

        minute = Math.Max(minute, latestEventMinute ?? 0);
        return Math.Clamp(minute, 0, matchLength);
    }

    /// <summary>The last minute that counts for minutes played: full length when finished, the clock when live, 0 otherwise.</summary>
    public int MatchEndMinute(
        MatchStatus status, DateTimeOffset? kickoffUtc, DateTimeOffset now, int? length, int? latestEventMinute) => status switch
    {
        MatchStatus.Finished => length ?? rules.DefaultMatchLengthMinutes,
        MatchStatus.Live when kickoffUtc is not null => LiveMinute(kickoffUtc.Value, now, length, latestEventMinute),
        _ => 0,
    };

    /// <summary>
    /// The legacy finalise gate, used only before migration 011 (settlement) is applied. Never finalises
    /// on "all fixtures final" alone (runbook Part 1b): the sync worker must have marked the gameweek
    /// complete, every fixture must be settled in the provider-state matrix (final, awarded, or won't
    /// be played this gameweek) and at least one must have a final result. With 011, SettlementService
    /// decides per fixture, and an admin's recorded resolution can stand in for the worker's evidence.
    /// </summary>
    public static bool CanFinalise(bool markedComplete, IReadOnlyCollection<short?> fixtureStates) =>
        markedComplete
        && fixtureStates.Any(MatchStatusExtensions.IsFinal)
        && fixtureStates.All(s => MatchStateMatrix.For(s).Settled);
}

/// <summary>Private league rules (section 10).</summary>
public sealed class LeagueRules(FantasyRules rules)
{
    /// <summary>
    /// "TL-" + 6 random digits with 2 special characters placed between digits, never next to
    /// each other and never first or last, e.g. TL-48#29!1.
    /// </summary>
    public string GenerateJoinCode(Random random)
    {
        var digits = Enumerable.Range(0, rules.JoinCodeDigits).Select(_ => (char)('0' + random.Next(10))).ToList();

        // Gaps between digits: gap i sits after digit i. Distinct gaps can't produce adjacent specials.
        var gaps = Enumerable.Range(0, rules.JoinCodeDigits - 1).OrderBy(_ => random.Next()).Take(rules.JoinCodeSpecialCount).ToHashSet();

        var body = new System.Text.StringBuilder();
        for (var i = 0; i < digits.Count; i++)
        {
            body.Append(digits[i]);
            if (gaps.Contains(i))
            {
                body.Append(rules.JoinCodeSpecialChars[random.Next(rules.JoinCodeSpecialChars.Length)]);
            }
        }
        return rules.JoinCodePrefix + body;
    }

    public bool IsValidJoinCode(string? code)
    {
        if (code is null || !code.StartsWith(rules.JoinCodePrefix, StringComparison.Ordinal)) return false;

        var body = code[rules.JoinCodePrefix.Length..];
        if (body.Length == 0 || !char.IsAsciiDigit(body[0]) || !char.IsAsciiDigit(body[^1])) return false;

        var digits = 0;
        var specials = 0;
        var previousWasSpecial = false;
        foreach (var ch in body)
        {
            if (char.IsAsciiDigit(ch))
            {
                digits++;
                previousWasSpecial = false;
            }
            else if (rules.JoinCodeSpecialChars.Contains(ch))
            {
                if (previousWasSpecial) return false;
                specials++;
                previousWasSpecial = true;
            }
            else
            {
                return false;
            }
        }
        return digits == rules.JoinCodeDigits && specials == rules.JoinCodeSpecialCount;
    }

    /// <summary>Generates codes until one isn't taken. Matching is exact, so uniqueness is exact too.</summary>
    public async Task<string> GenerateUniqueJoinCodeAsync(Func<string, Task<bool>> isTaken, Random random)
    {
        for (var attempt = 0; attempt < rules.JoinCodeMaxAttempts; attempt++)
        {
            var code = GenerateJoinCode(random);
            if (!await isTaken(code)) return code;
        }
        throw new InvalidOperationException($"Couldn't find a free join code after {rules.JoinCodeMaxAttempts} attempts.");
    }

    public RuleCheck CanJoin(int activeMembers, int maxMembers, bool alreadyMember)
    {
        var check = new RuleCheck();
        if (alreadyMember) check.Fail("Your team is already in this league.");
        else if (activeMembers >= maxMembers) check.Fail($"This league is full ({maxMembers} teams).");
        return check;
    }

    public static RuleCheck CanLeave(bool isOwner) =>
        isOwner ? new RuleCheck().Fail("You own this league, so you can't leave it.") : RuleCheck.Ok();

    /// <summary>Highest points first; ties go to overall points, then username (A–Z).</summary>
    public static IReadOnlyList<RankedEntry> Rank(IEnumerable<StandingInput> entries) =>
        entries
            .OrderByDescending(e => e.Points)
            .ThenByDescending(e => e.OverallPoints)
            .ThenBy(e => e.Username, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FantasyTeamId)
            .Select((e, i) => new RankedEntry(i + 1, e))
            .ToList();
}

public sealed record StandingInput(int FantasyTeamId, string TeamName, string Username, int Points, int OverallPoints, bool Provisional);

public sealed record RankedEntry(int Rank, StandingInput Entry);

/// <summary>
/// The squad version sent as an ETag and accepted in If-Match, so an offline change can't
/// silently overwrite a newer one. Format: "v{version}-{hash}" (the hash makes the ETag change
/// when anything else in the response changes, such as prices).
/// </summary>
public static class SquadVersion
{
    public static string ETag(int version, string hash) => $"\"v{version}-{hash}\"";

    /// <summary>Reads the version from an If-Match value. Null when the value isn't one of our squad ETags.</summary>
    public static int? Parse(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) return null;
        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal)) value = value[2..];
        value = value.Trim('"');
        if (!value.StartsWith('v')) return null;

        var dash = value.IndexOf('-');
        var number = dash < 0 ? value[1..] : value[1..dash];
        return int.TryParse(number, out var version) ? version : null;
    }

    public static IfMatchResult Check(string? ifMatch, int currentVersion)
    {
        if (string.IsNullOrWhiteSpace(ifMatch) || ifMatch.Trim() == "*") return IfMatchResult.NotSent;
        var version = Parse(ifMatch);
        if (version is null) return IfMatchResult.Unreadable;
        return version == currentVersion ? IfMatchResult.Current : IfMatchResult.Stale;
    }
}

public enum IfMatchResult
{
    NotSent,
    Current,
    Stale,
    Unreadable,
}
