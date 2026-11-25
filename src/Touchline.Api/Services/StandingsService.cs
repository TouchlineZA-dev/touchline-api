using Microsoft.EntityFrameworkCore;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed record OfficialStandingView(short? Position, short? Played, short? Points, short? GoalDifference, DateTimeOffset UpdatedAtUtc);

/// <param name="TieBreak">When level on points with a neighbour, what separated them: goal_difference, goals_for or name (still level).</param>
/// <param name="Deduction">Points deducted (from the official table: official points below the results-derived points with the same games played).</param>
public sealed record StandingRow(
    int Position,
    int TeamId,
    string Team,
    string? ShortCode,
    string? Crest,
    int Played,
    int Won,
    int Drawn,
    int Lost,
    int GoalsFor,
    int GoalsAgainst,
    int GoalDifference,
    int Points,
    int Deduction,
    IReadOnlyList<string> Form,
    string? TieBreak,
    OfficialStandingView? Official,
    string? Discrepancy);

public sealed record StandingsResponse(
    IdName Season,
    int Count,
    int MatchesCounted,
    int AwardedResultsCounted,
    string ComputedFrom,
    IReadOnlyList<StandingRow> Standings,
    IReadOnlyList<string> Notes,
    DateTimeOffset ServerTimeUtc);

/// <summary>
/// GET /api/v1/standings. Same table and tie rules as the Netlify standings function: built from
/// fixtures with a final result, ordered by points, goal difference, goals for, then name, with the
/// last five results as form. Additionally:
/// - awarded and walk-over results (state 17, 14) count with their recorded scoreline, without any
///   goal events being invented;
/// - football.standings (the worker's copy of the official table) is compared row by row: official
///   points below the derived points with the same games played are shown as a deduction and applied;
///   any other difference is reported as a discrepancy and the derived figures are kept.
/// </summary>
public sealed class StandingsService(TouchlineDbContext db, SchemaFeatures schema)
{
    public async Task<StandingsResponse> GetAsync(SeasonSummary season, DateTimeOffset now, CancellationToken ct)
    {
        var fixtures = await db.Fixtures
            .Where(f => f.SeasonId == season.Id && !f.IsPlaceholder && f.HomeTeamId != null && f.AwayTeamId != null)
            .OrderBy(f => f.StartingAt).ThenBy(f => f.Id)
            .ToListAsync(ct);
        var counted = fixtures
            .Where(f => MatchStateMatrix.For(f.StateId).ResultFinal && f.HomeScore is not null && f.AwayScore is not null)
            .ToList();

        var teamIds = counted.SelectMany(f => new[] { f.HomeTeamId!.Value, f.AwayTeamId!.Value }).Distinct().ToList();
        var teams = await db.Teams.Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var table = new Dictionary<int, Tally>();
        Tally? For(int id) => teams.ContainsKey(id) ? table.TryGetValue(id, out var t) ? t : table[id] = new Tally() : null;
        foreach (var f in counted)
        {
            var home = For(f.HomeTeamId!.Value);
            var away = For(f.AwayTeamId!.Value);
            if (home is null || away is null) continue;
            home.Add(f.HomeScore!.Value, f.AwayScore!.Value);
            away.Add(f.AwayScore!.Value, f.HomeScore!.Value);
        }

        var official = await OfficialAsync(season.Id, ct);
        var rows = table.Select(kv =>
            {
                var team = teams[kv.Key];
                var t = kv.Value;
                official.TryGetValue(kv.Key, out var o);
                var deduction = 0;
                string? discrepancy = null;
                if (o is not null)
                {
                    if (o.Played == t.Played && o.Points < t.Points) deduction = t.Points - o.Points!.Value;
                    else if (o.Played != t.Played || o.Points != t.Points)
                    {
                        discrepancy = $"Official table: played {o.Played}, points {o.Points}; results give played {t.Played}, points {t.Points}.";
                    }
                }
                return (Team: team, Tally: t, Official: o, Deduction: deduction, Discrepancy: discrepancy);
            })
            .OrderByDescending(r => r.Tally.Points - r.Deduction)
            .ThenByDescending(r => r.Tally.GoalsFor - r.Tally.GoalsAgainst)
            .ThenByDescending(r => r.Tally.GoalsFor)
            .ThenBy(r => r.Team.Name, StringComparer.CurrentCulture)
            .ToList();

        var standings = rows.Select((r, i) => new StandingRow(
            i + 1, r.Team.Id, r.Team.Name, r.Team.ShortCode, FootballService.ImageOrNull(r.Team.ImagePath),
            r.Tally.Played, r.Tally.Won, r.Tally.Drawn, r.Tally.Lost, r.Tally.GoalsFor, r.Tally.GoalsAgainst,
            r.Tally.GoalsFor - r.Tally.GoalsAgainst, r.Tally.Points - r.Deduction, r.Deduction,
            r.Tally.Form.TakeLast(5).ToList(),
            TieBreak(rows.Select(x => (x.Tally.Points - x.Deduction, x.Tally.GoalsFor - x.Tally.GoalsAgainst, x.Tally.GoalsFor)).ToList(), i),
            r.Official, r.Discrepancy)).ToList();

        return new StandingsResponse(
            new IdName(season.Id, season.Name),
            standings.Count,
            counted.Count,
            counted.Count(f => !MatchStateMatrix.For(f.StateId).Played),
            official.Count > 0 ? "finished-fixtures+official-deductions" : "finished-fixtures",
            standings,
            [
                "Figures are derived from fixtures with a final result (states 5, 7, 8) plus awarded/walk-over results (14, 17) with their recorded score.",
                "Deductions come from the official table (football.standings): official points below the derived points with the same games played.",
                "Any other difference from the official table is reported in 'discrepancy'; the derived figures are kept and an operator should review it.",
            ],
            now);
    }

    private async Task<Dictionary<int, OfficialStandingView>> OfficialAsync(int seasonId, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("football", "standings", ct)) return [];
        var rows = await db.Database.SqlQuery<OfficialRow>($"""
            SELECT team_id AS "TeamId", position AS "Position", played AS "Played", points AS "Points",
                   goal_difference AS "GoalDifference", updated_at AS "UpdatedAt"
            FROM football.standings WHERE season_id = {seasonId}
            """).ToListAsync(ct);
        return rows.GroupBy(r => r.TeamId)
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.OrderByDescending(x => x.UpdatedAt).First();
                return new OfficialStandingView(r.Position, r.Played, r.Points, r.GoalDifference, r.UpdatedAt);
            });
    }

    private static string? TieBreak(IReadOnlyList<(int Points, int Gd, int Gf)> ordered, int i)
    {
        var me = ordered[i];
        var neighbours = new[] { i - 1, i + 1 }.Where(j => j >= 0 && j < ordered.Count).Select(j => ordered[j]).Where(n => n.Points == me.Points).ToList();
        if (neighbours.Count == 0) return null;
        if (neighbours.Any(n => n.Gd == me.Gd && n.Gf == me.Gf)) return "name";
        if (neighbours.Any(n => n.Gd == me.Gd)) return "goals_for";
        return "goal_difference";
    }

    private sealed class Tally
    {
        public int Played, Won, Drawn, Lost, GoalsFor, GoalsAgainst, Points;
        public List<string> Form { get; } = [];

        public void Add(int scored, int conceded)
        {
            Played++;
            GoalsFor += scored;
            GoalsAgainst += conceded;
            if (scored > conceded) { Won++; Points += 3; Form.Add("W"); }
            else if (scored < conceded) { Lost++; Form.Add("L"); }
            else { Drawn++; Points += 1; Form.Add("D"); }
        }
    }

    internal sealed class OfficialRow
    {
        public int TeamId { get; set; }
        public short? Position { get; set; }
        public short? Played { get; set; }
        public short? Points { get; set; }
        public short? GoalDifference { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
