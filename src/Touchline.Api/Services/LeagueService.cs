using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>
/// Private leagues (join by code only, no public list) and the Overall league of every team in the
/// season, which has no table row and is worked out on request.
/// </summary>
public sealed class LeagueService(TouchlineDbContext db, PointsService points, IOptions<FantasyRules> options)
{
    public const string OverallId = "overall";

    private readonly FantasyRules _rules = options.Value;
    private readonly LeagueRules _leagueRules = new(options.Value);

    /// <summary>My leagues with my rank, plus Overall.</summary>
    public async Task<IReadOnlyList<LeagueSummaryView>> MyLeaguesAsync(FantasyContext ctx, CancellationToken ct)
    {
        if (ctx.Team is not { } team) return [];

        var seasonTeams = await SeasonTeamsAsync(ctx.Season.Id, ct);
        var totals = await points.TeamTotalsAsync(seasonTeams.Keys, ctx.Calendar, null, ctx.Now, ct);

        var leagueIds = await db.LeagueMembers
            .Where(m => m.FantasyTeamId == team.Id && m.LeftAt == null)
            .Select(m => m.LeagueId)
            .ToListAsync(ct);
        var myLeagues = await db.Leagues.Where(l => leagueIds.Contains(l.Id) && l.IsActive).OrderBy(l => l.Name).ToListAsync(ct);
        var members = await db.LeagueMembers
            .Where(m => leagueIds.Contains(m.LeagueId) && m.LeftAt == null)
            .Select(m => new { m.LeagueId, m.FantasyTeamId })
            .ToListAsync(ct);

        int? RankIn(IEnumerable<int> teamIds)
        {
            var ranked = LeagueRules.Rank(teamIds.Where(seasonTeams.ContainsKey).Select(id => Standing(id, seasonTeams, totals, useGameweek: false)));
            return ranked.FirstOrDefault(r => r.Entry.FantasyTeamId == team.Id)?.Rank;
        }

        var result = new List<LeagueSummaryView>
        {
            new(OverallId, "Overall", IsOverall: true, IsOwner: false, JoinCode: null, seasonTeams.Count, RankIn(seasonTeams.Keys),
                totals.Values.Any(t => t.Provisional)),
        };
        foreach (var league in myLeagues)
        {
            var teamIds = members.Where(m => m.LeagueId == league.Id).Select(m => m.FantasyTeamId).ToList();
            var isOwner = league.OwnerUserId == ctx.User.Id;
            result.Add(new LeagueSummaryView(
                league.Id.ToString(), league.Name, false, isOwner, isOwner ? league.JoinCode : null, teamIds.Count, RankIn(teamIds),
                teamIds.Any(id => totals.TryGetValue(id, out var t) && t.Provisional)));
        }
        return result;
    }

    /// <summary>Any user with a team can create a league. The owner's team joins automatically.</summary>
    public async Task<LeagueSummaryView> CreateAsync(FantasyContext ctx, CreateLeagueRequest request, CancellationToken ct)
    {
        var team = ctx.RequireTeam();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 50) throw new RuleViolationException("Give the league a name of 1 to 50 characters.");

        var code = await _leagueRules.GenerateUniqueJoinCodeAsync(c => db.Leagues.AnyAsync(l => l.JoinCode == c, ct), Random.Shared);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var league = new League
        {
            Name = name,
            JoinCode = code,
            OwnerUserId = ctx.User.Id,
            SeasonId = ctx.Season.Id,
            MaxMembers = (short)_rules.LeagueMaxMembers,
            IsActive = true,
        };
        db.Leagues.Add(league);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (FantasyContextService.IsUniqueViolation(ex))
        {
            throw new ConflictException("Another league took that join code at the same moment. Please try again.", "Try again");
        }
        db.LeagueMembers.Add(new LeagueMember { LeagueId = league.Id, FantasyTeamId = team.Id });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new LeagueSummaryView(league.Id.ToString(), league.Name, false, true, league.JoinCode, 1, 1, false);
    }

    /// <summary>
    /// Join by exact code. The league row is locked (SELECT … FOR UPDATE) while members are counted,
    /// so two people can't both take the 20th place.
    /// </summary>
    public async Task<LeagueSummaryView> JoinAsync(FantasyContext ctx, JoinLeagueRequest request, CancellationToken ct)
    {
        var team = ctx.RequireTeam();
        var code = request.Code?.Trim() ?? "";
        if (!_leagueRules.IsValidJoinCode(code))
        {
            throw new RuleViolationException("That isn't a league code. Codes look like TL-482#91!3.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var league = await db.Leagues
            .FromSql($"SELECT * FROM app.leagues WHERE join_code = {code} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(ct);
        if (league is null || !league.IsActive) throw new NotFoundException("No league has that code.");
        if (league.SeasonId != ctx.Season.Id) throw new RuleViolationException("That league is for a different season.");

        var membership = await db.LeagueMembers.AsTracking()
            .FirstOrDefaultAsync(m => m.LeagueId == league.Id && m.FantasyTeamId == team.Id, ct);
        var active = await db.LeagueMembers.CountAsync(m => m.LeagueId == league.Id && m.LeftAt == null, ct);

        var check = _leagueRules.CanJoin(active, league.MaxMembers, alreadyMember: membership is { LeftAt: null });
        if (!check.IsValid) throw new ConflictException(check.Errors[0], "Can't join");

        if (membership is null)
        {
            db.LeagueMembers.Add(new LeagueMember { LeagueId = league.Id, FantasyTeamId = team.Id });
        }
        else
        {
            membership.LeftAt = null; // rejoining after leaving
            membership.JoinedAt = ctx.Now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new LeagueSummaryView(league.Id.ToString(), league.Name, false, league.OwnerUserId == ctx.User.Id, null, active + 1, null, false);
    }

    /// <summary>Leaving sets left_at (no DELETE). The owner can't leave their own league.</summary>
    public async Task LeaveAsync(FantasyContext ctx, int leagueId, CancellationToken ct)
    {
        var team = ctx.RequireTeam();
        var league = await db.Leagues.FirstOrDefaultAsync(l => l.Id == leagueId, ct)
            ?? throw new NotFoundException("That league doesn't exist.");
        var membership = await db.LeagueMembers.AsTracking()
            .FirstOrDefaultAsync(m => m.LeagueId == leagueId && m.FantasyTeamId == team.Id && m.LeftAt == null, ct)
            ?? throw new NotFoundException("Your team isn't in that league.");

        var check = LeagueRules.CanLeave(isOwner: league.OwnerUserId == ctx.User.Id);
        if (!check.IsValid) throw new RuleViolationException(check.Errors);

        membership.LeftAt = ctx.Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Standings by season total, or a single gameweek with ?gameweek=N. Includes live provisional points.</summary>
    public async Task<StandingsView> StandingsAsync(FantasyContext ctx, string leagueId, int? gameweekNumber, CancellationToken ct)
    {
        GameweekInfo? gameweek = null;
        if (gameweekNumber is not null)
        {
            gameweek = ctx.Calendar.ByNumber(gameweekNumber.Value) ?? throw new NotFoundException($"Gameweek {gameweekNumber} doesn't exist.");
        }

        var seasonTeams = await SeasonTeamsAsync(ctx.Season.Id, ct);
        string name;
        List<int> teamIds;

        if (string.Equals(leagueId, OverallId, StringComparison.OrdinalIgnoreCase))
        {
            name = "Overall";
            teamIds = seasonTeams.Keys.ToList();
        }
        else
        {
            if (!int.TryParse(leagueId, out var id)) throw new NotFoundException("That league doesn't exist.");
            var league = await db.Leagues.FirstOrDefaultAsync(l => l.Id == id && l.IsActive, ct)
                ?? throw new NotFoundException("That league doesn't exist.");

            teamIds = await db.LeagueMembers.Where(m => m.LeagueId == id && m.LeftAt == null).Select(m => m.FantasyTeamId).ToListAsync(ct);
            if (ctx.Team is null || !teamIds.Contains(ctx.Team.Id))
            {
                throw new ForbiddenException("Only members can see this league's standings.");
            }
            name = league.Name;
        }

        var totals = await points.TeamTotalsAsync(teamIds, ctx.Calendar, gameweek, ctx.Now, ct);
        var ranked = LeagueRules.Rank(teamIds.Where(seasonTeams.ContainsKey).Select(t => Standing(t, seasonTeams, totals, useGameweek: gameweek is not null)));

        return new StandingsView(
            leagueId.ToLowerInvariant(),
            name,
            gameweek?.Number,
            ranked.Any(r => r.Entry.Provisional),
            ranked.Select(r => new StandingRowView(
                r.Rank, r.Entry.FantasyTeamId, r.Entry.TeamName, r.Entry.Username, r.Entry.Points, r.Entry.OverallPoints, r.Entry.Provisional)).ToList());
    }

    private static StandingInput Standing(
        int teamId, IReadOnlyDictionary<int, (string TeamName, string Username)> teams, IReadOnlyDictionary<int, TeamTotals> totals, bool useGameweek)
    {
        var t = totals.GetValueOrDefault(teamId, new TeamTotals(0, 0, false));
        var (teamName, username) = teams[teamId];
        return new StandingInput(teamId, teamName, username, useGameweek ? t.GameweekPoints : t.OverallPoints, t.OverallPoints, t.Provisional);
    }

    /// <summary>Every team in the season with its name and the owner's username.</summary>
    private async Task<Dictionary<int, (string TeamName, string Username)>> SeasonTeamsAsync(int seasonId, CancellationToken ct)
    {
        var rows = await (from t in db.FantasyTeams
                          join u in db.Users on t.UserId equals u.Id
                          where t.SeasonId == seasonId
                          select new { t.Id, t.Name, u.Username })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => (r.Name, r.Username));
    }
}
