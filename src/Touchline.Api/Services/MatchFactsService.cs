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
public sealed class MatchFactsService(TouchlineDbContext db, IOptions<FantasyRules> rules)
{
    private readonly GameweekRules _gameweekRules = new(rules.Value);

    public async Task<IReadOnlyList<PlayerFacts>> LoadAsync(IReadOnlyCollection<Fixture> fixtures, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Codes.MinutesCodesConfirmed) throw new SportmonksCodesNotConfirmedException();

        // Only live and finished fixtures can produce points (see MatchFactsDecoder.Counts).
        var counted = fixtures.Where(f => MatchFactsDecoder.Counts(MatchStatusExtensions.FromStateId(f.StateId))).ToList();
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

        var sportmonksIds = lineups.Select(l => l.SportmonksPlayerId!.Value)
            .Concat(events.SelectMany(e => new[] { e.SportmonksPlayerId, e.SportmonksRelatedPlayerId }).Where(id => id is not null).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var players = (await db.Players
                .Where(p => sportmonksIds.Contains(p.SportmonksPlayerId))
                .Select(p => new { p.Id, p.SportmonksPlayerId, p.PositionId })
                .ToListAsync(ct))
            .ToDictionary(p => p.SportmonksPlayerId, p => new DecoderPlayer(p.Id, p.PositionId));

        var latestEventMinute = await db.MatchEvents
            .Where(e => fixtureIds.Contains(e.FixtureId) && e.Minute != null)
            .GroupBy(e => e.FixtureId)
            .Select(g => new { FixtureId = g.Key, Minute = g.Max(e => e.Minute) })
            .ToDictionaryAsync(x => x.FixtureId, x => (int?)x.Minute, ct);

        return counted.SelectMany(fixture =>
        {
            var status = MatchStatusExtensions.FromStateId(fixture.StateId);
            var endMinute = _gameweekRules.MatchEndMinute(
                status, fixture.StartingAt, now, fixture.Length, latestEventMinute.GetValueOrDefault(fixture.Id));
            return MatchFactsDecoder.Decode(fixture, lineups, events, players, endMinute, startingXi, comingOnIsPlayerId);
        }).ToList();
    }
}
