using Microsoft.EntityFrameworkCore;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed class GameweekService(TouchlineDbContext db)
{
    public async Task<IReadOnlyList<GameweekSummary>> GetGameweeksAsync(int? seasonId, CancellationToken ct = default)
    {
        var query = db.Gameweeks.AsQueryable();
        if (seasonId is not null)
        {
            query = query.Where(g => g.SeasonId == seasonId);
        }

        var gameweeks = await query.OrderBy(g => g.StartDate).ToListAsync(ct);
        var ids = gameweeks.Select(g => g.Id).ToList();

        var readyIds = await db.GameweekSyncStatuses
            .Where(s => ids.Contains(s.GameweekId) && s.MarkedComplete)
            .Select(s => s.GameweekId)
            .ToListAsync(ct);
        var ready = readyIds.ToHashSet();

        return gameweeks
            .Select(g => new GameweekSummary(
                g.Id, g.Name, g.StartDate, g.EndDate, g.IsActive, g.SeasonId, ready.Contains(g.Id)))
            .ToList();
    }
}
