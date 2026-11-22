using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Business;

/// <summary>One player's facts in one fixture, keyed by football.players.id.</summary>
public sealed record PlayerFacts(int PlayerId, Position Position, PlayerFixtureFacts Facts);

/// <summary>A football.players row as the decoder needs it.</summary>
public sealed record DecoderPlayer(int PlayerId, short? PositionId);

/// <summary>
/// Turns one fixture's football.player_lineups and football.match_events rows into
/// PlayerFixtureFacts for the scoring rules. Pure: no database access, so every case can be
/// unit-tested with made-up rows. ID rule: lineups and events carry Sportmonks player ids; they are
/// bridged to football.players.id here and nothing downstream sees a Sportmonks id.
/// </summary>
public static class MatchFactsDecoder
{
    public static readonly IReadOnlyList<short> ScoringEventTypes =
    [
        Codes.EventTypes.Goal, Codes.EventTypes.OwnGoal, Codes.EventTypes.PenaltyScored, Codes.EventTypes.Substitution,
        Codes.EventTypes.YellowCard, Codes.EventTypes.RedCard, Codes.EventTypes.SecondYellowRed,
    ];

    /// <summary>
    /// Only live and finished fixtures produce points. Postponed, cancelled, abandoned, not started
    /// and "awaiting updates" fixtures produce nothing, even if some events were recorded for them
    /// (an abandoned match can have goals and cards from before it stopped).
    /// </summary>
    public static bool Counts(MatchStatus status) => status is MatchStatus.Live or MatchStatus.Finished;

    /// <param name="endMinute">The fixture's last minute so far: its length when finished, the current minute when live.</param>
    /// <param name="players">football.players rows by Sportmonks id. Players missing here can't be in a squad and are skipped.</param>
    public static IReadOnlyList<PlayerFacts> Decode(
        Fixture fixture,
        IEnumerable<PlayerLineup> lineups,
        IEnumerable<MatchEvent> events,
        IReadOnlyDictionary<long, DecoderPlayer> players,
        int endMinute,
        short startingXiType,
        bool comingOnIsPlayerId)
    {
        var status = MatchStatusExtensions.FromStateId(fixture.StateId);
        if (!Counts(status)) return [];

        var builders = new Dictionary<long, FactsBuilder>();
        FactsBuilder For(long sportmonksId, int? teamId)
        {
            if (!builders.TryGetValue(sportmonksId, out var b))
            {
                b = new FactsBuilder();
                builders[sportmonksId] = b;
            }
            b.TeamId ??= teamId;
            return b;
        }

        foreach (var lineup in lineups.Where(l => l.FixtureId == fixture.Id && l.SportmonksPlayerId is not null))
        {
            var b = For(lineup.SportmonksPlayerId!.Value, lineup.TeamId);
            b.Started |= lineup.TypeId == startingXiType;
        }

        var scoring = events
            .Where(e => e.FixtureId == fixture.Id && e.Rescinded != true && ScoringEventTypes.Contains(e.TypeId))
            .OrderBy(e => e.Minute ?? 0).ThenBy(e => e.ExtraMinute ?? 0).ThenBy(e => e.SortOrder ?? 0);

        foreach (var e in scoring)
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

        var finished = status == MatchStatus.Finished;
        var result = new List<PlayerFacts>();
        foreach (var (sportmonksId, b) in builders)
        {
            if (!players.TryGetValue(sportmonksId, out var player)) continue;

            // Unknown team → unknown conceded → no clean sheet.
            int? conceded = null;
            if (b.TeamId is not null && b.TeamId == fixture.HomeTeamId) conceded = fixture.AwayScore ?? 0;
            else if (b.TeamId is not null && b.TeamId == fixture.AwayTeamId) conceded = fixture.HomeScore ?? 0;

            result.Add(new PlayerFacts(player.PlayerId, PositionExtensions.FromSportmonks(player.PositionId), new PlayerFixtureFacts
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
