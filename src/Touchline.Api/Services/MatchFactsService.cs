using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Services;

/// <summary>One player's facts in one fixture, keyed by football.players.id.</summary>
public sealed record PlayerFacts(int PlayerId, Position Position, PlayerFixtureFacts Facts);

/// <summary>
/// Decodes football.player_lineups and football.match_events into PlayerFixtureFacts for the pure
/// scoring rules. ID rule: lineups and events carry Sportmonks player ids; everything is bridged to
/// football.players.id here, so nothing downstream ever sees a Sportmonks id.
/// </summary>
public sealed class MatchFactsService(TouchlineDbContext db, IOptions<FantasyRules> rules)
{
    private static readonly short[] ScoringEventTypes =
    [
        Codes.EventTypes.Goal, Codes.EventTypes.OwnGoal, Codes.EventTypes.PenaltyScored, Codes.EventTypes.Substitution,
        Codes.EventTypes.YellowCard, Codes.EventTypes.RedCard, Codes.EventTypes.SecondYellowRed,
    ];

    private readonly GameweekRules _gameweekRules = new(rules.Value);

    public async Task<IReadOnlyList<PlayerFacts>> LoadAsync(IReadOnlyCollection<Fixture> fixtures, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Codes.MinutesCodesConfirmed) throw new SportmonksCodesNotConfirmedException();
        if (fixtures.Count == 0) return [];

        var startingXi = Codes.LineupTypes.StartingXi!.Value;
        var comingOnIsPlayerId = Codes.Substitutions.PlayerIdIsComingOn!.Value;

        var fixtureIds = fixtures.Select(f => f.Id).ToList();
        var lineups = await db.PlayerLineups
            .Where(l => fixtureIds.Contains(l.FixtureId) && l.SportmonksPlayerId != null)
            .ToListAsync(ct);
        var events = await db.MatchEvents
            .Where(e => fixtureIds.Contains(e.FixtureId) && ScoringEventTypes.Contains(e.TypeId) && e.Rescinded != true)
            .ToListAsync(ct);

        var sportmonksIds = lineups.Select(l => l.SportmonksPlayerId!.Value)
            .Concat(events.SelectMany(e => new[] { e.SportmonksPlayerId, e.SportmonksRelatedPlayerId }).Where(id => id is not null).Select(id => id!.Value))
            .Distinct()
            .ToList();
        var players = await db.Players
            .Where(p => sportmonksIds.Contains(p.SportmonksPlayerId))
            .Select(p => new { p.Id, p.SportmonksPlayerId, p.PositionId })
            .ToListAsync(ct);
        var bySportmonksId = players.ToDictionary(p => p.SportmonksPlayerId);

        var latestEventMinute = await db.MatchEvents
            .Where(e => fixtureIds.Contains(e.FixtureId) && e.Minute != null)
            .GroupBy(e => e.FixtureId)
            .Select(g => new { FixtureId = g.Key, Minute = g.Max(e => e.Minute) })
            .ToDictionaryAsync(x => x.FixtureId, x => (int?)x.Minute, ct);

        var result = new List<PlayerFacts>();
        foreach (var fixture in fixtures)
        {
            var status = MatchStatusExtensions.FromStateId(fixture.StateId);
            var endMinute = _gameweekRules.MatchEndMinute(
                status, fixture.StartingAt, now, fixture.Length, latestEventMinute.GetValueOrDefault(fixture.Id));
            var finished = status == MatchStatus.Finished;

            var fixtureEvents = events.Where(e => e.FixtureId == fixture.Id).ToList();
            var builders = new Dictionary<long, FactsBuilder>();

            FactsBuilder For(long sportmonksId, int? teamId)
            {
                if (!builders.TryGetValue(sportmonksId, out var b))
                {
                    b = new FactsBuilder { TeamId = teamId };
                    builders[sportmonksId] = b;
                }
                b.TeamId ??= teamId;
                return b;
            }

            foreach (var lineup in lineups.Where(l => l.FixtureId == fixture.Id))
            {
                var b = For(lineup.SportmonksPlayerId!.Value, lineup.TeamId);
                b.Started |= lineup.TypeId == startingXi;
            }

            foreach (var e in fixtureEvents.OrderBy(e => e.Minute ?? 0).ThenBy(e => e.ExtraMinute ?? 0))
            {
                var minute = e.Minute ?? 0;
                switch (e.TypeId)
                {
                    case Codes.EventTypes.Substitution:
                        var onId = comingOnIsPlayerId ? e.SportmonksPlayerId : e.SportmonksRelatedPlayerId;
                        var offId = comingOnIsPlayerId ? e.SportmonksRelatedPlayerId : e.SportmonksPlayerId;
                        if (onId is not null) For(onId.Value, e.TeamId).CameOn ??= minute;
                        if (offId is not null) For(offId.Value, e.TeamId).LeaveAt(minute);
                        break;
                    case Codes.EventTypes.Goal:
                    case Codes.EventTypes.PenaltyScored:
                        if (e.SportmonksPlayerId is { } scorer)
                        {
                            var b = For(scorer, null);
                            if (e.TypeId == Codes.EventTypes.Goal) b.Goals++; else b.Penalties++;
                        }
                        // The assist is the goal event's related player, when present.
                        if (e.SportmonksRelatedPlayerId is { } assister) For(assister, null).Assists++;
                        break;
                    case Codes.EventTypes.OwnGoal:
                        if (e.SportmonksPlayerId is { } ownGoalScorer) For(ownGoalScorer, null).OwnGoals++;
                        break;
                    case Codes.EventTypes.YellowCard:
                        if (e.SportmonksPlayerId is { } booked) For(booked, null).Yellows++;
                        break;
                    case Codes.EventTypes.RedCard:
                        if (e.SportmonksPlayerId is { } sentOff)
                        {
                            var b = For(sentOff, null);
                            b.Red = true;
                            b.LeaveAt(minute);
                        }
                        break;
                    case Codes.EventTypes.SecondYellowRed:
                        if (e.SportmonksPlayerId is { } secondYellow)
                        {
                            var b = For(secondYellow, null);
                            b.SecondYellow = true;
                            b.LeaveAt(minute);
                        }
                        break;
                }
            }

            foreach (var (sportmonksId, b) in builders)
            {
                // Events can name players not in football.players yet; they can't be in any squad.
                if (!bySportmonksId.TryGetValue(sportmonksId, out var player)) continue;

                // Unknown team → unknown conceded → no clean sheet. Not started → nothing conceded yet, but no clean sheet either.
                int? conceded = null;
                if (status is MatchStatus.Finished or MatchStatus.Live)
                {
                    if (b.TeamId is not null && b.TeamId == fixture.HomeTeamId) conceded = fixture.AwayScore ?? 0;
                    else if (b.TeamId is not null && b.TeamId == fixture.AwayTeamId) conceded = fixture.HomeScore ?? 0;
                }

                result.Add(new PlayerFacts(player.Id, PositionExtensions.FromSportmonks(player.PositionId), new PlayerFixtureFacts
                {
                    FixtureId = fixture.Id,
                    Started = b.Started,
                    CameOnMinute = b.Started ? null : b.CameOn,
                    WentOffMinute = b.WentOff,
                    MatchEndMinute = endMinute,
                    FixtureFinished = finished,
                    Goals = b.Goals,
                    PenaltiesScored = b.Penalties,
                    Assists = b.Assists,
                    OwnGoals = b.OwnGoals,
                    YellowCards = b.Yellows,
                    RedCard = b.Red,
                    SecondYellow = b.SecondYellow,
                    GoalsConceded = conceded,
                }));
            }
        }
        return result;
    }

    private sealed class FactsBuilder
    {
        public int? TeamId;
        public bool Started;
        public int? CameOn;
        public int? WentOff;
        public int Goals, Penalties, Assists, OwnGoals, Yellows;
        public bool Red, SecondYellow;

        public void LeaveAt(int minute) => WentOff = WentOff is null ? minute : Math.Min(WentOff.Value, minute);
    }
}
