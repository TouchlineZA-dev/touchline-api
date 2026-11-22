using Microsoft.EntityFrameworkCore;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>Loads players as the fantasy rules see them: position, current club and current price.</summary>
public sealed class FantasyDataService(TouchlineDbContext db, FootballService football, AvailabilityService availability)
{
    public async Task<Dictionary<int, FantasyPlayer>> LoadPlayersAsync(int seasonId, IEnumerable<int> playerIds, CancellationToken ct = default)
    {
        var ids = playerIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var players = await db.Players.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        var priceRows = await db.PlayerPrices.Where(p => p.SeasonId == seasonId && ids.Contains(p.PlayerId)).ToListAsync(ct);
        var prices = priceRows.GroupBy(p => p.PlayerId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.UpdatedAt).First().Price);

        var clubs = await football.GetLatestClubsAsync(seasonId, ct);
        var clubIds = players.Select(p => clubs.TryGetValue(p.SportmonksPlayerId, out var c) ? c : (int?)null)
            .Where(c => c is not null).Select(c => c!.Value).Distinct().ToList();
        var clubNames = await db.Teams.Where(t => clubIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var available = await availability.GetAsync(seasonId, ct);

        return players.ToDictionary(p => p.Id, p =>
        {
            var position = PositionExtensions.FromSportmonks(p.PositionId);
            int? clubId = clubs.TryGetValue(p.SportmonksPlayerId, out var c) ? c : null;
            return new FantasyPlayer(
                p.Id,
                FootballService.NameOf(p),
                position,
                clubId,
                clubId is not null && clubNames.TryGetValue(clubId.Value, out var name) ? name : null,
                prices.TryGetValue(p.Id, out var price) ? price : football.DefaultPrice(position),
                available.IsAvailable(p.Id, p.SportmonksPlayerId));
        });
    }
}

/// <summary>A team's squad for one gameweek: saved rows, or a copy of the latest earlier gameweek.</summary>
public sealed record LoadedSquad(FantasyGameweekEntry Entry, IReadOnlyList<FantasySquadPick> Picks, bool Saved)
{
    public SquadSelection ToSelection() => SquadStore.ToSelection(Entry.Formation, Picks);
}

/// <summary>
/// Reads and writes app.fantasy_squads and app.fantasy_gameweek_entries. A team's picks are copied
/// forward from its previous gameweek when it has none for a new one. No DELETE: rows are updated.
/// </summary>
public sealed class SquadStore(TouchlineDbContext db)
{
    public Task<bool> HasAnySquadAsync(int teamId, CancellationToken ct) =>
        db.FantasySquads.AnyAsync(p => p.FantasyTeamId == teamId, ct);

    /// <summary>Read-only view. When the gameweek has no saved picks, shows the copy that would be made (not saved).</summary>
    public async Task<LoadedSquad?> LoadAsync(int teamId, GameweekInfo gameweek, SeasonCalendar calendar, CancellationToken ct = default)
    {
        var picks = await db.FantasySquads.Where(p => p.FantasyTeamId == teamId && p.GameweekId == gameweek.Id).ToListAsync(ct);
        var entry = await db.GameweekEntries.FirstOrDefaultAsync(e => e.FantasyTeamId == teamId && e.GameweekId == gameweek.Id, ct);
        if (picks.Count > 0)
        {
            return new LoadedSquad(entry ?? await NewEntryFromPicksAsync(teamId, gameweek.Id, picks, ct), picks, Saved: true);
        }

        var source = await SourceGameweekAsync(teamId, gameweek, calendar, ct);
        if (source is null) return null;

        var sourcePicks = await db.FantasySquads.Where(p => p.FantasyTeamId == teamId && p.GameweekId == source.Id).ToListAsync(ct);
        var sourceEntry = await db.GameweekEntries.FirstOrDefaultAsync(e => e.FantasyTeamId == teamId && e.GameweekId == source.Id, ct);
        var copy = CopyPicks(sourcePicks, gameweek.Id);
        var formation = sourceEntry?.Formation ?? (await NewEntryFromPicksAsync(teamId, gameweek.Id, sourcePicks, ct)).Formation;
        return new LoadedSquad(NewEntry(teamId, gameweek.Id, formation), copy, Saved: false);
    }

    /// <summary>
    /// Tracked rows for writing: copies the previous gameweek forward if needed and, once the
    /// gameweek has locked, fills a missing captain or vice-captain at random from the starting XI.
    /// Saves before returning. Null when the team has never picked a squad.
    /// </summary>
    public async Task<LoadedSquad?> EnsureAsync(
        int teamId, GameweekInfo gameweek, SeasonCalendar calendar, DateTimeOffset now, CancellationToken ct = default)
    {
        var picks = await db.FantasySquads.AsTracking()
            .Where(p => p.FantasyTeamId == teamId && p.GameweekId == gameweek.Id).ToListAsync(ct);
        var entry = await db.GameweekEntries.AsTracking()
            .FirstOrDefaultAsync(e => e.FantasyTeamId == teamId && e.GameweekId == gameweek.Id, ct);

        if (picks.Count == 0)
        {
            var source = await SourceGameweekAsync(teamId, gameweek, calendar, ct);
            if (source is null) return null;

            var sourcePicks = await db.FantasySquads.Where(p => p.FantasyTeamId == teamId && p.GameweekId == source.Id).ToListAsync(ct);
            var sourceEntry = await db.GameweekEntries.FirstOrDefaultAsync(e => e.FantasyTeamId == teamId && e.GameweekId == source.Id, ct);
            picks = CopyPicks(sourcePicks, gameweek.Id);
            db.FantasySquads.AddRange(picks);

            if (entry is null)
            {
                var formation = sourceEntry?.Formation ?? (await NewEntryFromPicksAsync(teamId, gameweek.Id, sourcePicks, ct)).Formation;
                entry = NewEntry(teamId, gameweek.Id, formation);
                db.GameweekEntries.Add(entry);
            }
        }
        else if (entry is null)
        {
            entry = await NewEntryFromPicksAsync(teamId, gameweek.Id, picks, ct);
            db.GameweekEntries.Add(entry);
        }

        if (gameweek.IsLocked(now))
        {
            var selection = ToSelection(entry.Formation, picks);
            var filled = CaptaincyRules.EnsureCaptains(selection, teamId, gameweek.Id);
            if (!ReferenceEquals(filled, selection))
            {
                await ApplySelectionAsync(picks, filled, ct);
                entry.Version++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new LoadedSquad(entry, picks, Saved: true);
    }

    /// <summary>
    /// Writes starters, bench order and captaincy onto existing rows. Captaincy is cleared and saved
    /// first, so the one-captain-per-gameweek indexes (V031) never see two captains mid-update.
    /// </summary>
    public async Task ApplySelectionAsync(IReadOnlyList<FantasySquadPick> picks, SquadSelection selection, CancellationToken ct)
    {
        foreach (var pick in picks)
        {
            pick.IsCaptain = false;
            pick.IsViceCaptain = false;
        }
        await db.SaveChangesAsync(ct);

        foreach (var pick in picks)
        {
            var benchIndex = selection.Bench.ToList().IndexOf(pick.PlayerId);
            pick.IsOnBench = benchIndex >= 0;
            pick.BenchOrder = benchIndex >= 0 ? (short)(benchIndex + 1) : null;
            pick.IsCaptain = pick.PlayerId == selection.CaptainId;
            pick.IsViceCaptain = pick.PlayerId == selection.ViceCaptainId;
        }
    }

    public static SquadSelection ToSelection(string formation, IReadOnlyList<FantasySquadPick> picks) => new(
        formation,
        picks.Where(p => !p.IsOnBench).OrderBy(p => p.Id).Select(p => p.PlayerId).ToList(),
        picks.Where(p => p.IsOnBench).OrderBy(p => p.BenchOrder ?? short.MaxValue).ThenBy(p => p.Id).Select(p => p.PlayerId).ToList(),
        picks.FirstOrDefault(p => p.IsCaptain)?.PlayerId,
        picks.FirstOrDefault(p => p.IsViceCaptain)?.PlayerId);

    public static FantasyGameweekEntry NewEntry(int teamId, int gameweekId, string formation) =>
        new() { FantasyTeamId = teamId, GameweekId = gameweekId, Formation = formation, Version = 1 };

    /// <summary>The latest earlier gameweek this team has picks for.</summary>
    private async Task<GameweekInfo?> SourceGameweekAsync(int teamId, GameweekInfo gameweek, SeasonCalendar calendar, CancellationToken ct)
    {
        var earlier = calendar.Gameweeks.Where(g => g.Number < gameweek.Number).Select(g => g.Id).ToList();
        var withPicks = await db.FantasySquads
            .Where(p => p.FantasyTeamId == teamId && earlier.Contains(p.GameweekId))
            .Select(p => p.GameweekId)
            .Distinct()
            .ToListAsync(ct);
        return calendar.Gameweeks.Where(g => withPicks.Contains(g.Id)).MaxBy(g => g.Number);
    }

    private static List<FantasySquadPick> CopyPicks(IEnumerable<FantasySquadPick> source, int gameweekId) =>
        source.OrderBy(p => p.Id).Select(p => new FantasySquadPick
        {
            FantasyTeamId = p.FantasyTeamId,
            PlayerId = p.PlayerId,
            GameweekId = gameweekId,
            IsCaptain = p.IsCaptain,
            IsViceCaptain = p.IsViceCaptain,
            IsOnBench = p.IsOnBench,
            BenchOrder = p.BenchOrder,
            PurchasePrice = p.PurchasePrice,
        }).ToList();

    /// <summary>For picks saved before entries existed: the formation is read from the starters' positions.</summary>
    private async Task<FantasyGameweekEntry> NewEntryFromPicksAsync(int teamId, int gameweekId, IReadOnlyList<FantasySquadPick> picks, CancellationToken ct)
    {
        var starterIds = picks.Where(p => !p.IsOnBench).Select(p => p.PlayerId).ToList();
        var positions = await db.Players.Where(p => starterIds.Contains(p.Id)).Select(p => p.PositionId).ToListAsync(ct);
        var decoded = positions.Select(PositionExtensions.FromSportmonks).ToList();
        var formation = new Formation(
            decoded.Count(p => p == Position.DEF), decoded.Count(p => p == Position.MID), decoded.Count(p => p == Position.ATT));
        return NewEntry(teamId, gameweekId, formation.ToString());
    }
}
