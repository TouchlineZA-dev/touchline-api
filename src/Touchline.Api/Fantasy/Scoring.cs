using Touchline.Api.Business;

namespace Touchline.Api.Fantasy;

/// <summary>
/// What happened to one player in one fixture, already decoded from lineups and events.
/// Turning database rows into these facts is done in Services (it depends on Sportmonks codes);
/// everything from here on is pure.
/// </summary>
public sealed record PlayerFixtureFacts
{
    public int FixtureId { get; init; }

    /// <summary>In the starting XI.</summary>
    public bool Started { get; init; }
    /// <summary>Minute he came on as a substitute, if he did.</summary>
    public int? CameOnMinute { get; init; }
    /// <summary>Minute he left the pitch: subbed off or sent off.</summary>
    public int? WentOffMinute { get; init; }

    /// <summary>Last minute of the match so far: the fixture length (90 if null) when finished, the current minute when live.</summary>
    public int MatchEndMinute { get; init; }
    /// <summary>The result is final (state 5, 7 or 8).</summary>
    public bool FixtureFinished { get; init; }

    public int Goals { get; init; }
    public int PenaltiesScored { get; init; }
    public int Assists { get; init; }
    public int OwnGoals { get; init; }
    public int YellowCards { get; init; }
    public bool RedCard { get; init; }
    /// <summary>Second yellow (type 21). Counts as a red only.</summary>
    public bool SecondYellow { get; init; }

    /// <summary>Goals his team conceded in this fixture (so far, if live). Null when unknown.</summary>
    public int? GoalsConceded { get; init; }
}

/// <summary>Points for one player, summed over the fixtures in a gameweek. Stored as JSON in app.points_results.breakdown.</summary>
public sealed record PointsBreakdown
{
    public int Fixtures { get; init; }
    public int Minutes { get; init; }
    public int Appearance { get; init; }
    public int Goals { get; init; }
    public int GoalPoints { get; init; }
    public int Assists { get; init; }
    public int AssistPoints { get; init; }
    public int CleanSheets { get; init; }
    public int CleanSheetPoints { get; init; }
    public int YellowCards { get; init; }
    public int YellowPoints { get; init; }
    public int RedCards { get; init; }
    public int RedPoints { get; init; }
    public int OwnGoals { get; init; }
    public int OwnGoalPoints { get; init; }
    public int Total { get; init; }
    /// <summary>True while any fixture counted here is not final (live or provisional clean sheet).</summary>
    public bool Provisional { get; init; }

    public static readonly PointsBreakdown None = new();

    public PointsBreakdown Add(PointsBreakdown other) => new()
    {
        Fixtures = Fixtures + other.Fixtures,
        Minutes = Minutes + other.Minutes,
        Appearance = Appearance + other.Appearance,
        Goals = Goals + other.Goals,
        GoalPoints = GoalPoints + other.GoalPoints,
        Assists = Assists + other.Assists,
        AssistPoints = AssistPoints + other.AssistPoints,
        CleanSheets = CleanSheets + other.CleanSheets,
        CleanSheetPoints = CleanSheetPoints + other.CleanSheetPoints,
        YellowCards = YellowCards + other.YellowCards,
        YellowPoints = YellowPoints + other.YellowPoints,
        RedCards = RedCards + other.RedCards,
        RedPoints = RedPoints + other.RedPoints,
        OwnGoals = OwnGoals + other.OwnGoals,
        OwnGoalPoints = OwnGoalPoints + other.OwnGoalPoints,
        Total = Total + other.Total,
        Provisional = Provisional || other.Provisional,
    };
}

/// <summary>Player points per fixture and per gameweek (section 7).</summary>
public sealed class PlayerScoring(FantasyRules rules)
{
    /// <summary>
    /// Minutes on the pitch: from 0 if he started or from the minute he came on, until the
    /// minute he went off (sub or red card) or the end of the match so far.
    /// </summary>
    public static int Minutes(PlayerFixtureFacts f)
    {
        int? from = f.Started ? 0 : f.CameOnMinute;
        if (from is null) return 0;

        var start = Math.Clamp(from.Value, 0, f.MatchEndMinute);
        var end = Math.Clamp(f.WentOffMinute ?? f.MatchEndMinute, start, f.MatchEndMinute);
        return end - start;
    }

    /// <summary>On the pitch from kickoff to the final whistle of a finished match.</summary>
    public static bool PlayedFullMatch(PlayerFixtureFacts f) =>
        f.Started && f.WentOffMinute is null && f.FixtureFinished;

    public int AppearancePoints(PlayerFixtureFacts f)
    {
        if (PlayedFullMatch(f)) return rules.FullMatchPoints;
        return Minutes(f) >= rules.AppearanceMinMinutes ? rules.AppearancePoints : 0;
    }

    public PointsBreakdown ScoreFixture(PlayerFixtureFacts f, Position position)
    {
        var minutes = Minutes(f);
        var appearance = AppearancePoints(f);

        var goals = f.Goals + f.PenaltiesScored;
        var assists = rules.AssistsEnabled ? f.Assists : 0;

        var cleanSheet = position is Position.GK or Position.DEF
            && minutes >= rules.CleanSheetMinMinutes
            && f.GoalsConceded == 0;

        // A second yellow counts as a red only: no extra deduction for the yellows.
        var yellows = f.SecondYellow ? 0 : f.YellowCards;
        var red = f.RedCard || f.SecondYellow;

        var breakdown = new PointsBreakdown
        {
            Fixtures = 1,
            Minutes = minutes,
            Appearance = appearance,
            Goals = goals,
            GoalPoints = goals * rules.GoalPoints,
            Assists = assists,
            AssistPoints = assists * rules.AssistPoints,
            CleanSheets = cleanSheet ? 1 : 0,
            CleanSheetPoints = cleanSheet ? rules.CleanSheetPoints : 0,
            YellowCards = yellows,
            YellowPoints = yellows * rules.YellowCardPoints,
            RedCards = red ? 1 : 0,
            RedPoints = red ? rules.RedCardPoints : 0,
            OwnGoals = f.OwnGoals,
            OwnGoalPoints = f.OwnGoals * rules.OwnGoalPoints,
            Provisional = !f.FixtureFinished,
        };

        return breakdown with
        {
            Total = breakdown.Appearance + breakdown.GoalPoints + breakdown.AssistPoints + breakdown.CleanSheetPoints
                    + breakdown.YellowPoints + breakdown.RedPoints + breakdown.OwnGoalPoints,
        };
    }

    /// <summary>All fixtures in a gameweek added up (double gameweeks score twice).</summary>
    public PointsBreakdown ScoreGameweek(IEnumerable<PlayerFixtureFacts> fixtures, Position position) =>
        fixtures.Aggregate(PointsBreakdown.None, (sum, f) => sum.Add(ScoreFixture(f, position)));
}

/// <summary>One pick in a team for the gameweek being scored, with that player's gameweek points.</summary>
public sealed record TeamPickPoints(
    int PlayerId,
    bool IsStarter,
    bool IsCaptain,
    bool IsViceCaptain,
    PointsBreakdown Points);

public sealed record PickScore(int PlayerId, int BasePoints, int Multiplier, bool Counted, int Contribution);

public sealed record TeamGameweekScore(
    int Total,
    int PointsBeforeHits,
    int BenchPoints,
    int TransferCost,
    bool Provisional,
    IReadOnlyList<PickScore> Picks);

/// <summary>Team points for a gameweek (sections 3 and 8). No auto-subs.</summary>
public sealed class TeamScoring(FantasyRules rules)
{
    public TeamGameweekScore Score(IReadOnlyList<TeamPickPoints> picks, string? chip, int transferCost)
    {
        var benchBoost = chip == Chips.BenchBoost;
        var tripleCaptain = chip == Chips.TripleCaptain;
        var armband = tripleCaptain ? rules.TripleCaptainMultiplier : rules.CaptainMultiplier;

        var captain = picks.FirstOrDefault(p => p.IsCaptain);
        var captainPlayed = captain is not null && captain.Points.Minutes > 0;

        int MultiplierFor(TeamPickPoints p)
        {
            if (p.IsCaptain) return captainPlayed ? armband : 1;
            // The vice-captain takes the armband (Triple Captain included) when the captain didn't play.
            if (p.IsViceCaptain) return captainPlayed ? rules.ViceCaptainMultiplier : armband;
            return 1;
        }

        var scores = picks.Select(p =>
        {
            // No auto-subs: a starter who didn't play just scores 0. Bench only counts with Bench Boost.
            var counted = p.IsStarter || benchBoost;
            var multiplier = counted ? MultiplierFor(p) : 0;
            return new PickScore(p.PlayerId, p.Points.Total, multiplier, counted, p.Points.Total * multiplier);
        }).ToList();

        var beforeHits = scores.Sum(s => s.Contribution);
        return new TeamGameweekScore(
            Total: beforeHits - transferCost,
            PointsBeforeHits: beforeHits,
            BenchPoints: picks.Where(p => !p.IsStarter).Sum(p => p.Points.Total),
            TransferCost: transferCost,
            Provisional: picks.Any(p => p.Points.Provisional),
            Picks: scores);
    }
}

/// <summary>If a team reaches the deadline with no captain or vice-captain, pick at random from the starting XI.</summary>
public static class CaptaincyRules
{
    public static SquadSelection EnsureCaptains(SquadSelection selection, Random random)
    {
        var captain = selection.CaptainId is { } c && selection.Starters.Contains(c) ? c : (int?)null;
        var vice = selection.ViceCaptainId is { } v && selection.Starters.Contains(v) && v != captain ? v : (int?)null;
        if (captain is not null && vice is not null) return selection;

        var pool = selection.Starters.ToList();
        if (captain is null)
        {
            var candidates = pool.Where(id => id != vice).ToList();
            captain = candidates[random.Next(candidates.Count)];
        }
        if (vice is null)
        {
            var candidates = pool.Where(id => id != captain).ToList();
            vice = candidates[random.Next(candidates.Count)];
        }

        return selection with { CaptainId = captain, ViceCaptainId = vice };
    }
}
