using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Services;

/// <summary>
/// Loads football.player_lineups, football.match_events and football.players for a set of fixtures
/// and hands each fixture to the pure MatchFactsDecoder. Read only.
/// </summary>
public sealed class MatchFactsService(TouchlineDbContext db, PlayerIdBridge bridge, IOptions<FantasyRules> rules)
{
    private readonly MatchClock _clock = new(rules.Value);

    public async Task<IReadOnlyList<PlayerFacts>> LoadAsync(IReadOnlyCollection<Fixture> fixtures, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Codes.MinutesCodesConfirmed) throw new SportmonksCodesNotConfirmedException();

        // Only live and finished fixtures can produce points (see MatchFactsDecoder.Counts).
        var counted = fixtures.Where(f => MatchFactsDecoder.Counts(f.StateId)).ToList();
        if (counted.Count == 0) return [];

        var startingXi = Codes.LineupTypes.StartingXi!.Value;
        var comingOnIsPlayerId = Codes.Substitutions.PlayerIdIsComingOn!.Value;
        var scoringTypes = MatchFactsDecoder.ScoringEventTypes.ToArray();

        var fixtureIds = counted.Select(f => f.Id).ToList();
        var lineups = await db.PlayerLineups
            .Where(l => fixtureIds.Contains(l.FixtureId) && l.SportmonksPlayerId != null)
            .ToListAsync(ct);
        var events = await db.MatchEvents
            .Where(e => fixtureIds.Contains(e.FixtureId) && scoringTypes.Contains(e.TypeId) && e.Rescinded != true)
            .ToListAsync(ct);

        var players = (await bridge.ResolveAsync(PlayerIdBridge.IdsIn(lineups, events), ct))
            .ToDictionary(p => p.Key, p => new DecoderPlayer(p.Value.Id, p.Value.PositionId));

        var latestEventMinute = await db.MatchEvents
            .Where(e => fixtureIds.Contains(e.FixtureId) && e.Minute != null)
            .GroupBy(e => e.FixtureId)
            .Select(g => new { FixtureId = g.Key, Minute = g.Max(e => e.Minute) })
            .ToDictionaryAsync(x => x.FixtureId, x => (int?)x.Minute, ct);

        return counted.SelectMany(fixture =>
        {
            // The same clock the match detail shows (MatchClock), so scoring and the timeline agree.
            var endMinute = _clock.Read(fixture.StateId, fixture.StartingAt, fixture.Length, latestEventMinute.GetValueOrDefault(fixture.Id), now).Minute ?? 0;
            return MatchFactsDecoder.Decode(fixture, lineups, events, players, endMinute, startingXi, comingOnIsPlayerId);
        }).ToList();
    }
}
