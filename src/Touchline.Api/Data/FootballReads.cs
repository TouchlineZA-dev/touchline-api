using Microsoft.EntityFrameworkCore;
using Touchline.Api.Models;

namespace Touchline.Api.Data;

/// <summary>
/// The one way the API reads football.match_events and football.player_lineups. The sync worker marks
/// an event the provider stopped listing (VAR reversal, a correction reissued under a new id) with
/// match_events.withdrawn_at (touchline-sync V040), and a player dropped from a complete XI with
/// player_lineups.withdrawn_at (V041), instead of deleting rows. Readers must filter
/// withdrawn_at IS NULL. Before those migrations run, the column is missing and every row is current.
/// </summary>
public sealed class FootballReads(TouchlineDbContext db, SchemaFeatures schema)
{
    public async Task<IQueryable<MatchEvent>> EventsAsync(CancellationToken ct = default) =>
        await schema.HasColumnAsync("football", "match_events", "withdrawn_at", ct)
            ? db.MatchEvents.FromSql($"SELECT * FROM football.match_events WHERE withdrawn_at IS NULL")
            : db.MatchEvents;

    public async Task<IQueryable<PlayerLineup>> LineupsAsync(CancellationToken ct = default) =>
        await schema.HasColumnAsync("football", "player_lineups", "withdrawn_at", ct)
            ? db.PlayerLineups.FromSql($"SELECT * FROM football.player_lineups WHERE withdrawn_at IS NULL")
            : db.PlayerLineups;

    /// <summary>For raw SQL over player_lineups (alias l): the filter to append, or empty before V041.</summary>
    public async Task<bool> LineupsHaveWithdrawnAsync(CancellationToken ct = default) =>
        await schema.HasColumnAsync("football", "player_lineups", "withdrawn_at", ct);
}
