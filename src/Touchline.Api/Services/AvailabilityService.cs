using Microsoft.EntityFrameworkCore;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>Who can be picked and priced in a season.</summary>
public sealed class AvailabilitySet(IReadOnlySet<long> inLineups, IReadOnlyDictionary<int, bool> overrides, int lineupSeasonId)
{
    /// <summary>The season whose lineups were used: the target season, or the previous one before the target has any.</summary>
    public int LineupSeasonId { get; } = lineupSeasonId;

    public bool IsAvailable(int playerId, long sportmonksPlayerId) =>
        PlayerAvailability.IsAvailable(inLineups.Contains(sportmonksPlayerId), overrides.TryGetValue(playerId, out var o) ? o : null);
}

/// <summary>
/// Players are pickable and priced when they've been in a PSL matchday squad (lineup type 11 or 12)
/// in the season, unless an admin override says otherwise. Before a season has any lineups (a
/// pre-season price run), the previous PSL season's lineups are used.
/// </summary>
public sealed class AvailabilityService(TouchlineDbContext db, FootballReads reads, ILogger<AvailabilityService> logger)
{
    public async Task<AvailabilitySet> GetAsync(int seasonId, CancellationToken ct = default)
    {
        var lineupSeason = seasonId;
        var inLineups = await LineupPlayersAsync(seasonId, ct);
        if (inLineups.Count == 0 && await PreviousPslSeasonAsync(seasonId, ct) is { } previous)
        {
            lineupSeason = previous;
            inLineups = await LineupPlayersAsync(previous, ct);
        }
        return new AvailabilitySet(inLineups, await OverridesAsync(seasonId, ct), lineupSeason);
    }

    /// <summary>Admin: mark a player available or not for a season. Insert only; the newest row wins.</summary>
    public async Task<PlayerAvailabilityOverride> SetOverrideAsync(
        int playerId, int seasonId, bool available, string reason, int adminUserId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(reason)) errors.Add("Every availability override needs a reason.");
        if (!await db.Players.AnyAsync(p => p.Id == playerId, ct)) errors.Add($"Player {playerId} doesn't exist.");
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct)) errors.Add($"Season {seasonId} doesn't exist.");
        if (errors.Count > 0) throw new RuleViolationException(errors);

        var row = new PlayerAvailabilityOverride
        {
            PlayerId = playerId,
            SeasonId = seasonId,
            IsAvailable = available,
            Reason = reason.Trim(),
            CreatedByUserId = adminUserId,
        };
        db.AvailabilityOverrides.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    private async Task<HashSet<long>> LineupPlayersAsync(int seasonId, CancellationToken ct)
    {
        short[] squadTypes = [SportmonksCodes.LineupTypes.StartingXi!.Value, SportmonksCodes.LineupTypes.Bench!.Value];
        var ids = await (from l in await reads.LineupsAsync(ct)
                         join f in db.Fixtures on l.FixtureId equals f.Id
                         where f.SeasonId == seasonId && l.SportmonksPlayerId != null && squadTypes.Contains(l.TypeId)
                         select l.SportmonksPlayerId!.Value).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    private async Task<int?> PreviousPslSeasonAsync(int seasonId, CancellationToken ct)
    {
        var season = await db.Seasons.FirstOrDefaultAsync(s => s.Id == seasonId, ct);
        if (season?.StartingAt is null) return null;
        return await db.Seasons
            .Where(s => s.CompetitionId == season.CompetitionId && s.StartingAt < season.StartingAt)
            .OrderByDescending(s => s.StartingAt)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Latest override per player. Empty (with a warning) until docs/migrations/006 is applied. The table
    /// is checked first rather than catching the error, because a failed query would abort the
    /// transaction this may be running in (transfers, squad saves).
    /// </summary>
    private async Task<Dictionary<int, bool>> OverridesAsync(int seasonId, CancellationToken ct)
    {
        var exists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass('app.player_availability_overrides') IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);
        if (!exists)
        {
            logger.LogWarning("app.player_availability_overrides doesn't exist yet (docs/migrations/006); no availability overrides applied.");
            return [];
        }

        var rows = await db.AvailabilityOverrides.Where(o => o.SeasonId == seasonId)
            .Select(o => new { o.PlayerId, o.IsAvailable, o.CreatedAt, o.Id })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.PlayerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First().IsAvailable);
    }
}
