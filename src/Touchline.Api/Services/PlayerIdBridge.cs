using Microsoft.EntityFrameworkCore;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>A football.players row as seen through its Sportmonks id.</summary>
public sealed record BridgedPlayer(int Id, long SportmonksPlayerId, string Name, short? PositionId, string? ImageUrl);

/// <summary>
/// THE place where Sportmonks player ids (football.match_events, football.player_lineups) become
/// internal football.players ids (BACKEND_INTEGRATION.md §7). Scoring and match detail both use it.
/// A null id, or one with no football.players row, stays unresolved: the row is still shown (by the
/// provider's name) and counted for the team, but no player gets points for it.
/// </summary>
public sealed class PlayerIdBridge(TouchlineDbContext db)
{
    public async Task<IReadOnlyDictionary<long, BridgedPlayer>> ResolveAsync(IEnumerable<long?> sportmonksIds, CancellationToken ct = default)
    {
        var ids = sportmonksIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, BridgedPlayer>();

        var players = await db.Players.Where(p => ids.Contains(p.SportmonksPlayerId)).ToListAsync(ct);
        return players
            .GroupBy(p => p.SportmonksPlayerId)
            .ToDictionary(g => g.Key, g =>
            {
                var p = g.OrderBy(x => x.Id).First();
                return new BridgedPlayer(p.Id, p.SportmonksPlayerId, FootballService.NameOf(p), p.PositionId, FootballService.ImageOrNull(p.ImagePath));
            });
    }

    public static IEnumerable<long?> IdsIn(IEnumerable<PlayerLineup> lineups, IEnumerable<MatchEvent> events) =>
        lineups.Select(l => l.SportmonksPlayerId)
            .Concat(events.Select(e => e.SportmonksPlayerId))
            .Concat(events.Select(e => e.SportmonksRelatedPlayerId));
}
