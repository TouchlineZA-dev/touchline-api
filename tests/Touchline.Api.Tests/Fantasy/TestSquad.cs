using Touchline.Api.Business;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Tests.Fantasy;

/// <summary>
/// A valid 15-player pool: 2 GK (1–2), 5 DEF (11–15), 5 MID (21–25), 3 ATT (31–33), plus spares
/// for transfers. Clubs are spread so no club has more than 3. Squad cost R93.0m.
/// </summary>
public static class TestSquad
{
    public static readonly FantasyRules Rules = new();

    public static Dictionary<int, FantasyPlayer> Players()
    {
        var players = new Dictionary<int, FantasyPlayer>();
        void Add(int id, Position pos, int club, decimal price) =>
            players[id] = new FantasyPlayer(id, $"Player {id}", pos, club, $"Club {club}", price);

        Add(1, Position.GK, 1, 4.5m);
        Add(2, Position.GK, 2, 4.5m);
        for (var i = 0; i < 5; i++) Add(11 + i, Position.DEF, 3 + i % 3, 5.0m);   // clubs 3,4,5,3,4
        for (var i = 0; i < 5; i++) Add(21 + i, Position.MID, 6 + i % 3, 7.0m);   // clubs 6,7,8,6,7
        for (var i = 0; i < 3; i++) Add(31 + i, Position.ATT, 9 + i, 8.0m);       // clubs 9,10,11

        // Spares for transfers (not in the squad).
        Add(3, Position.GK, 12, 4.0m);
        Add(16, Position.DEF, 12, 4.5m);
        Add(26, Position.MID, 12, 6.0m);
        Add(34, Position.ATT, 12, 9.0m);
        Add(99, Position.Unknown, 12, 4.0m);
        return players;
    }

    public static readonly int[] Goalkeepers = [1, 2];
    public static readonly int[] Defenders = [11, 12, 13, 14, 15];
    public static readonly int[] Midfielders = [21, 22, 23, 24, 25];
    public static readonly int[] Attackers = [31, 32, 33];

    /// <summary>A valid selection in the given formation. Captain = first starting midfielder, vice = first defender.</summary>
    public static SquadSelection Selection(string formation = "4-4-2")
    {
        Formation.TryParse(formation, out var f);
        var starters = new List<int> { 1 };
        starters.AddRange(Defenders.Take(f.Defenders));
        starters.AddRange(Midfielders.Take(f.Midfielders));
        starters.AddRange(Attackers.Take(f.Attackers));

        var bench = new List<int> { 2 };
        bench.AddRange(Defenders.Skip(f.Defenders));
        bench.AddRange(Midfielders.Skip(f.Midfielders));
        bench.AddRange(Attackers.Skip(f.Attackers));

        return new SquadSelection(formation, starters, bench, CaptainId: 21, ViceCaptainId: 11);
    }

    public static SquadRules SquadRules() => new(Rules);
}
