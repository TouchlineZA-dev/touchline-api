using Touchline.Api.Business;

namespace Touchline.Api.Fantasy;

/// <summary>A starting formation, DEF-MID-ATT (the goalkeeper is implied), e.g. 4-4-2.</summary>
public readonly record struct Formation(int Defenders, int Midfielders, int Attackers)
{
    public int Outfield => Defenders + Midfielders + Attackers;

    public int Count(Position position) => position switch
    {
        Position.GK => 1,
        Position.DEF => Defenders,
        Position.MID => Midfielders,
        Position.ATT => Attackers,
        _ => 0,
    };

    public override string ToString() => $"{Defenders}-{Midfielders}-{Attackers}";

    public static bool TryParse(string? text, out Formation formation)
    {
        formation = default;
        var parts = (text ?? "").Split('-');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var d) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var a))
        {
            return false;
        }
        formation = new Formation(d, m, a);
        return true;
    }
}

/// <summary>
/// What the rules need to know about a player. Position comes from Sportmonks and can't be changed.
/// Available: the player can be picked (see PlayerAvailability).
/// </summary>
public sealed record FantasyPlayer(
    int PlayerId,
    string Name,
    Position Position,
    int? ClubId,
    string? ClubName,
    decimal Price,
    bool Available = true);

/// <summary>
/// Who can be picked and priced: players who have been in a current-season PSL matchday squad
/// (lineup type 11 or 12), unless an admin override says otherwise (e.g. a new signing who hasn't
/// made a squad yet, or a player who has left).
/// </summary>
public static class PlayerAvailability
{
    public static bool IsAvailable(bool inCurrentSeasonLineups, bool? adminOverride) => adminOverride ?? inCurrentSeasonLineups;

    public static string NotAvailableMessage(string name) =>
        $"{name} isn't available to pick: he hasn't been in a PSL matchday squad this season.";
}

/// <summary>
/// A full squad as the user arranges it: 11 starters, 4 bench players in order,
/// a captain and a vice-captain. Player ids are football.players.id.
/// </summary>
public sealed record SquadSelection(
    string Formation,
    IReadOnlyList<int> Starters,
    IReadOnlyList<int> Bench,
    int? CaptainId,
    int? ViceCaptainId)
{
    public IEnumerable<int> AllPlayers => Starters.Concat(Bench);
}

public static class Chips
{
    public const string BenchBoost = "bench_boost";
    public const string TripleCaptain = "triple_captain";
    public const string Wildcard = "wildcard";

    public static readonly IReadOnlyList<string> All = [BenchBoost, TripleCaptain, Wildcard];

    public static bool IsValid(string? chip) => chip is not null && All.Contains(chip);
}

/// <summary>The outcome of a rule check: every broken rule, in plain language, plus non-blocking warnings.</summary>
public sealed class RuleCheck
{
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public bool IsValid => _errors.Count == 0;

    public RuleCheck Fail(string error)
    {
        if (!_errors.Contains(error)) _errors.Add(error);
        return this;
    }

    public RuleCheck Warn(string warning)
    {
        if (!_warnings.Contains(warning)) _warnings.Add(warning);
        return this;
    }

    public RuleCheck Merge(RuleCheck other)
    {
        foreach (var e in other.Errors) Fail(e);
        foreach (var w in other.Warnings) Warn(w);
        return this;
    }

    public static RuleCheck Ok() => new();
}

public static class PositionNames
{
    public static string Plural(Position position, int count) => position switch
    {
        Position.GK => count == 1 ? "goalkeeper" : "goalkeepers",
        Position.DEF => count == 1 ? "defender" : "defenders",
        Position.MID => count == 1 ? "midfielder" : "midfielders",
        Position.ATT => count == 1 ? "attacker" : "attackers",
        _ => count == 1 ? "player with an unknown position" : "players with an unknown position",
    };

    public static readonly IReadOnlyList<Position> Playable = [Position.GK, Position.DEF, Position.MID, Position.ATT];
}
