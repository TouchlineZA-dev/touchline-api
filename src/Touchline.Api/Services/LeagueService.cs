using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;

namespace Touchline.Api.Services;

/// <summary>
/// Private leagues (join by code only, no public list) and the Overall league of every team in the
/// season, which has no table row and is worked out on request.
/// </summary>
public sealed class LeagueService(
    TouchlineDbContext db, PointsService points, Users.AccountService accounts, SchemaFeatures schema,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache, IOptions<FantasyRules> options)
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
        // Suspended managers are hidden from the public Overall league (they stay in private leagues).
        var suspended = await accounts.SuspendedTeamIdsAsync(ctx.Season.Id, ct);
        var overallTeams = seasonTeams.Keys.Where(id => !suspended.Contains(id) || id == team.Id).ToList();

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
            new(OverallId, "Overall", IsOverall: true, IsOwner: false, JoinCode: null, overallTeams.Count, RankIn(overallTeams),
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

    /// <summary>
    /// The owner hands the league to another active member (the owner can't leave, so this is how an owner
    /// departs). Nothing is deleted.
    /// </summary>
    public async Task<LeagueSummaryView> TransferOwnershipAsync(FantasyContext ctx, int leagueId, int toFantasyTeamId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var league = await db.Leagues.FromSql($"SELECT * FROM app.leagues WHERE id = {leagueId} FOR UPDATE").AsTracking().FirstOrDefaultAsync(ct);
        if (league is null || !league.IsActive) throw new NotFoundException("That league doesn't exist.");
        if (league.OwnerUserId != ctx.User.Id) throw new ForbiddenException("Only the league owner can hand it over.");
        var target = await (from m in db.LeagueMembers
                            join t in db.FantasyTeams on m.FantasyTeamId equals t.Id
                            where m.LeagueId == leagueId && m.FantasyTeamId == toFantasyTeamId && m.LeftAt == null
                            select new { t.UserId }).FirstOrDefaultAsync(ct)
            ?? throw new RuleViolationException("The new owner must be a current member of the league.");
        if (target.UserId == ctx.User.Id) throw new RuleViolationException("You already own this league.");
        league.OwnerUserId = target.UserId;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var members = await db.LeagueMembers.CountAsync(m => m.LeagueId == leagueId && m.LeftAt == null, ct);
        return new LeagueSummaryView(league.Id.ToString(), league.Name, false, false, null, members, null, false);
    }

    /// <summary>The owner archives the league: it disappears from members' lists and can't be joined; history is kept (no DELETE).</summary>
    public async Task ArchiveAsync(FantasyContext ctx, int leagueId, CancellationToken ct)
    {
        var league = await db.Leagues.AsTracking().FirstOrDefaultAsync(l => l.Id == leagueId && l.IsActive, ct)
            ?? throw new NotFoundException("That league doesn't exist.");
        if (league.OwnerUserId != ctx.User.Id) throw new ForbiddenException("Only the league owner can archive it.");
        league.IsActive = false;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>My rank in a league after each finalised gameweek (members only; "overall" works too).</summary>
    public async Task<object> RankHistoryAsync(FantasyContext ctx, string leagueId, CancellationToken ct)
    {
        var team = ctx.RequireTeam();
        List<int> teamIds;
        if (string.Equals(leagueId, OverallId, StringComparison.OrdinalIgnoreCase))
        {
            teamIds = await db.FantasyTeams.Where(t => t.SeasonId == ctx.Season.Id).Select(t => t.Id).ToListAsync(ct);
        }
        else
        {
            if (!int.TryParse(leagueId, out var id)) throw new NotFoundException("That league doesn't exist.");
            teamIds = await db.LeagueMembers.Where(m => m.LeagueId == id && m.LeftAt == null).Select(m => m.FantasyTeamId).ToListAsync(ct);
            if (!teamIds.Contains(team.Id)) throw new ForbiddenException("Only members can see this league's history.");
        }
        var users = await (from t in db.FantasyTeams join u in db.Users on t.UserId equals u.Id where teamIds.Contains(t.Id)
                           select new { t.Id, t.Name, u.Username }).ToDictionaryAsync(x => x.Id, ct);
        var entries = await db.GameweekEntries.Where(e => teamIds.Contains(e.FantasyTeamId) && e.ScoredAt != null && e.Points != null)
            .Select(e => new { e.FantasyTeamId, e.GameweekId, Points = e.Points!.Value }).ToListAsync(ct);
        var running = teamIds.ToDictionary(t => t, _ => 0);
        var rows = new List<object>();
        foreach (var gw in ctx.Calendar.Gameweeks)
        {
            var week = entries.Where(e => e.GameweekId == gw.Id).ToList();
            if (week.Count == 0) continue;
            foreach (var e in week) running[e.FantasyTeamId] += e.Points;
            var ranked = LeagueRules.Rank(running.Select(kv => new StandingInput(kv.Key, users[kv.Key].Name, users[kv.Key].Username,
                kv.Value, kv.Value, false)));
            rows.Add(new { gameweekId = gw.Id, gameweek = gw.Number, rank = ranked.First(r => r.Entry.FantasyTeamId == team.Id).Rank, points = running[team.Id] });
        }
        return new { leagueId = leagueId.ToLowerInvariant(), teamId = team.Id, history = rows };
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
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<StandingsView> StandingsAsync(FantasyContext ctx, string leagueId, int? gameweekNumber, CancellationToken ct, int page = 1, int pageSize = DefaultPageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var revision = await RevisionLabelAsync(ctx, ct);
        var key = $"standings:{ctx.Season.Id}:{leagueId.ToLowerInvariant()}:{gameweekNumber}:{revision}";
        if (cache.TryGetValue(key, out StandingsView? cached) && cached is not null)
        {
            if (!string.Equals(leagueId, OverallId, StringComparison.OrdinalIgnoreCase)
                && (ctx.Team is null || !cached.Rows.Any(r => r.FantasyTeamId == ctx.Team.Id)))
            {
                throw new ForbiddenException("Only members can see this league's standings.");
            }
            return Page(cached, page, pageSize, ctx.Team?.Id);
        }
        var full = await FullStandingsAsync(ctx, leagueId, gameweekNumber, ct);
        full = full with { Revision = revision + (full.Provisional ? "+live" : "") };
        // Shared standings are cached by revision (final points only change with a new published revision);
        // while provisional points are included, only for the live-points cache period.
        cache.Set(key, full, TimeSpan.FromSeconds(full.Provisional ? options.Value.LivePointsCacheSeconds : 300));
        return Page(full, page, pageSize, ctx.Team?.Id);
    }

    private static StandingsView Page(StandingsView full, int page, int pageSize, int? myTeamId) => full with
    {
        Rows = full.Rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
        Page = page,
        PageSize = pageSize,
        TotalRows = full.Rows.Count,
        Me = myTeamId is { } id ? full.Rows.FirstOrDefault(r => r.FantasyTeamId == id) : null,
    };

    /// <summary>"r{newest published revision id of the season}", or "legacy" before migration 011.</summary>
    private async Task<string> RevisionLabelAsync(FantasyContext ctx, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("app", "score_revisions", ct)) return "legacy";
        var ids = ctx.Calendar.Gameweeks.Select(g => g.Id).ToArray();
        var max = await db.Database.SqlQuery<int?>($"""
            SELECT MAX(id) AS "Value" FROM app.score_revisions WHERE status = 'published' AND gameweek_id = ANY({ids})
            """).FirstOrDefaultAsync(ct);
        return $"r{max ?? 0}";
    }

    private async Task<StandingsView> FullStandingsAsync(FantasyContext ctx, string leagueId, int? gameweekNumber, CancellationToken ct)
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
            // Suspended managers are hidden from the public Overall league (docs/accounts/policies.md).
            var suspended = await accounts.SuspendedTeamIdsAsync(ctx.Season.Id, ct);
            teamIds = seasonTeams.Keys.Where(id => !suspended.Contains(id)).ToList();
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
        var members = teamIds.Where(seasonTeams.ContainsKey).ToList();
        var ranked = LeagueRules.Rank(members.Select(t => Standing(t, seasonTeams, totals, useGameweek: gameweek is not null)));

        // Rank movement: the ranking before this gameweek's points (same tie rules).
        var previous = gameweek is null
            ? LeagueRules.Rank(members.Select(t =>
              {
                  var s = Standing(t, seasonTeams, totals, useGameweek: false);
                  var week = totals.GetValueOrDefault(t, new TeamTotals(0, 0, false)).GameweekPoints;
                  return s with { Points = s.Points - week, OverallPoints = s.OverallPoints - week };
              })).ToDictionary(r => r.Entry.FantasyTeamId, r => r.Rank)
            : null;

        return new StandingsView(
            leagueId.ToLowerInvariant(),
            name,
            gameweek?.Number,
            ranked.Any(r => r.Entry.Provisional),
            ranked.Select(r =>
            {
                int? before = previous is not null && previous.TryGetValue(r.Entry.FantasyTeamId, out var p) ? p : null;
                return new StandingRowView(r.Rank, r.Entry.FantasyTeamId, r.Entry.TeamName, r.Entry.Username, r.Entry.Points, r.Entry.OverallPoints,
                    r.Entry.Provisional, before, before - r.Rank);
            }).ToList());
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
