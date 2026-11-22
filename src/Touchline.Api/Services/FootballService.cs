using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

public sealed class FootballService(
    TouchlineDbContext db, IOptions<FootballOptions> options, IOptions<FantasyRules> fantasy)
{
    /// <summary>
    /// Price when app.player_prices has no row: the position's minimum price (Fantasy:Pricing),
    /// the same price a player with no history gets. Replaces the old 50.0 default, which can't
    /// work with a R300.0m budget.
    /// </summary>
    public decimal DefaultPrice(Position position) =>
        position == Position.Unknown ? fantasy.Value.Pricing.Goalkeeper.Min : fantasy.Value.Pricing.RangeFor(position).Min;

    public Task<SeasonSummary?> GetCurrentPslSeasonAsync(CancellationToken ct = default)
    {
        var pslId = options.Value.PslSportmonksCompetitionId;

        return (from c in db.Competitions
                join s in db.Seasons on c.Id equals s.CompetitionId
                where c.SportmonksCompetitionId == pslId && s.IsCurrent
                select new SeasonSummary(s.Id, s.Name, s.CompetitionId, s.StartingAt, s.EndingAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<TeamSummary>> GetTeamsAsync(CancellationToken ct = default)
    {
        var teams = await db.Teams
            .Where(t => !t.IsPlaceholder)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        return teams.Select(t => new TeamSummary(t.Id, t.Name, t.ShortCode, RealImageOrNull(t.ImagePath))).ToList();
    }

    public async Task<IReadOnlyList<PlayerSummary>> GetPlayersAsync(int seasonId, CancellationToken ct = default)
    {
        var players = await db.Players.ToListAsync(ct);

        var prices = await db.PlayerPrices
            .Where(p => p.SeasonId == seasonId)
            .ToDictionaryAsync(p => p.PlayerId, p => p.Price, ct);

        var teams = await db.Teams.ToDictionaryAsync(t => t.Id, ct);

        var clubBySportmonksId = await GetLatestClubsAsync(seasonId, ct);

        return players
            .Select(p =>
            {
                var position = PositionExtensions.FromSportmonks(p.PositionId);
                Team? team = null;
                if (clubBySportmonksId.TryGetValue(p.SportmonksPlayerId, out var teamId))
                {
                    teams.TryGetValue(teamId, out team);
                }

                return new PlayerSummary(
                    Id: p.Id,
                    SportmonksPlayerId: p.SportmonksPlayerId,
                    Name: PlayerName(p),
                    Position: position,
                    TeamId: team?.Id,
                    Team: team?.Name,
                    TeamCrest: RealImageOrNull(team?.ImagePath),
                    NationalityId: p.NationalityId,
                    ImageUrl: RealImageOrNull(p.ImagePath),
                    Price: prices.TryGetValue(p.Id, out var price) ? price : DefaultPrice(position));
            })
            .OrderBy(p => p.Name)
            .ToList();
    }

    /// <summary>
    /// football.players has no club column, so a player's club is the team he most recently lined
    /// up for this season (same approach as the Netlify players function). Keyed by Sportmonks id;
    /// see the ID-space rule.
    /// </summary>
    public async Task<Dictionary<long, int>> GetLatestClubsAsync(int seasonId, CancellationToken ct = default)
    {
        var clubs = await db.Database.SqlQuery<PlayerClubRow>($"""
            SELECT DISTINCT ON (l.sportmonks_player_id)
                   l.sportmonks_player_id AS "SportmonksPlayerId",
                   l.team_id              AS "TeamId"
            FROM football.player_lineups l
            JOIN football.fixtures f ON f.id = l.fixture_id
            WHERE f.season_id = {seasonId}
              AND l.sportmonks_player_id IS NOT NULL
              AND l.team_id IS NOT NULL
            ORDER BY l.sportmonks_player_id, f.starting_at DESC NULLS LAST
            """).ToListAsync(ct);

        return clubs.ToDictionary(c => c.SportmonksPlayerId, c => c.TeamId);
    }

    /// <summary>Display name: display_name, then common_name, then first + last.</summary>
    public static string NameOf(Player p) => PlayerName(p);

    /// <summary>Null for Sportmonks' stock "placeholder" image.</summary>
    public static string? ImageOrNull(string? url) => RealImageOrNull(url);

    public async Task<IReadOnlyList<FixtureSummary>> GetFixturesAsync(
        int seasonId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        var query = db.Fixtures.Where(f => f.SeasonId == seasonId);

        // Npgsql only writes UTC DateTimeOffset values to timestamptz.
        if (from is not null)
        {
            var fromUtc = from.Value.ToUniversalTime();
            query = query.Where(f => f.StartingAt >= fromUtc);
        }
        if (to is not null)
        {
            var toUtc = to.Value.ToUniversalTime();
            query = query.Where(f => f.StartingAt <= toUtc);
        }

        var fixtures = await query.OrderBy(f => f.StartingAt).ToListAsync(ct);
        var teamNames = await db.Teams.ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        string NameOf(int? id) => id is not null && teamNames.TryGetValue(id.Value, out var n) ? n : "TBD";

        return fixtures
            .Select(f => new FixtureSummary(
                f.Id,
                f.SportmonksFixtureId,
                f.StartingAt,
                MatchStatusExtensions.FromStateId(f.StateId),
                f.StateId,
                f.HomeTeamId,
                NameOf(f.HomeTeamId),
                f.AwayTeamId,
                NameOf(f.AwayTeamId),
                f.HomeScore,
                f.AwayScore))
            .ToList();
    }

    /// <summary>display_name, then common_name, then first + last (Netlify players function order).</summary>
    private static string PlayerName(Player p)
    {
        if (!string.IsNullOrWhiteSpace(p.DisplayName)) return p.DisplayName;
        if (!string.IsNullOrWhiteSpace(p.CommonName)) return p.CommonName;

        var full = string.Join(' ', new[] { p.Firstname, p.Lastname }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return full.Length > 0 ? full : "Unknown player";
    }

    /// <summary>Sportmonks returns a stock "placeholder" URL when there is no image (BACKEND_INTEGRATION.md §12).</summary>
    private static string? RealImageOrNull(string? url) =>
        string.IsNullOrWhiteSpace(url) || url.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ? null : url;
}

/// <summary>Row shape for the latest-club raw SQL query. Property names match the quoted column aliases.</summary>
internal sealed class PlayerClubRow
{
    public long SportmonksPlayerId { get; set; }
    public int TeamId { get; set; }
}
