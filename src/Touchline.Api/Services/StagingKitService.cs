using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Models;
using Touchline.Api.Users;

namespace Touchline.Api.Services;

/// <summary>Staging kit settings ("StagingKit" section). Never enabled in Production.</summary>
public sealed class StagingKitOptions
{
    public const string SectionName = "StagingKit";

    public bool Enabled { get; set; }

    /// <summary>Supabase user ids (sub) of the two staging reference managers, created in the staging Supabase project by its owner.</summary>
    public string[] ManagerSubjects { get; set; } = [];
}

/// <summary>
/// The mobile developer's staging kit (03-Leruo-API Stage 6): two reference managers with teams, a
/// private league that can be reset (archive the old one, create a new one; nothing is deleted) and
/// stable fixture ids to test against. Admin only, and only where StagingKit:Enabled is true outside Production.
/// </summary>
public sealed class StagingKitService(
    TouchlineDbContext db,
    UserService users,
    FootballService football,
    GameweekService gameweeks,
    IOptions<StagingKitOptions> options,
    IOptions<FantasyRules> rules,
    IHostEnvironment environment,
    TimeProvider time)
{
    public const string LeagueName = "Staging kit league";

    public void RequireEnabled()
    {
        if (environment.IsProduction() || !options.Value.Enabled)
        {
            throw new NotFoundException("The staging kit isn't enabled in this environment.");
        }
    }

    public async Task<object> ResetAsync(CancellationToken ct)
    {
        RequireEnabled();
        var season = await football.GetCurrentPslSeasonAsync(ct) ?? throw new NotFoundException("No current PSL season.");
        var now = time.GetUtcNow();

        var managers = new List<(AppUser User, FantasyTeam Team, string? Subject)>();
        for (var i = 0; i < 2; i++)
        {
            var subject = options.Value.ManagerSubjects.ElementAtOrDefault(i);
            var sub = Guid.TryParse(subject, out var parsed) ? parsed : DeterministicSubject(i);
            var user = await users.GetOrCreateAsync(sub, null, ct);
            var team = await db.FantasyTeams.AsTracking().FirstOrDefaultAsync(t => t.UserId == user.Id && t.SeasonId == season.Id, ct);
            if (team is null)
            {
                team = new FantasyTeam { UserId = user.Id, SeasonId = season.Id, Name = $"Reference XI {i + 1}", BudgetRemaining = rules.Value.Budget };
                db.FantasyTeams.Add(team);
                await db.SaveChangesAsync(ct);
            }
            managers.Add((user, team, Guid.TryParse(subject, out _) ? subject : null));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var old = await db.Leagues.AsTracking().Where(l => l.OwnerUserId == managers[0].User.Id && l.Name == LeagueName && l.IsActive).ToListAsync(ct);
        foreach (var league in old) league.IsActive = false;
        var code = await new LeagueRules(rules.Value).GenerateUniqueJoinCodeAsync(c => db.Leagues.AnyAsync(l => l.JoinCode == c, ct), Random.Shared);
        var fresh = new League { Name = LeagueName, JoinCode = code, OwnerUserId = managers[0].User.Id, SeasonId = season.Id, MaxMembers = (short)rules.Value.LeagueMaxMembers };
        db.Leagues.Add(fresh);
        await db.SaveChangesAsync(ct);
        foreach (var m in managers) db.LeagueMembers.Add(new LeagueMember { LeagueId = fresh.Id, FantasyTeamId = m.Team.Id });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var calendar = await gameweeks.GetCalendarAsync(season.Id, ct);
        var week = calendar.Next(now) ?? calendar.Current(now);
        return new
        {
            managers = managers.Select(m => new { userId = m.User.Id, teamId = m.Team.Id, teamName = m.Team.Name, supabaseSubject = m.Subject,
                canSignIn = m.Subject is not null }),
            league = new { id = fresh.Id, joinCode = fresh.JoinCode, archivedLeagueIds = old.Select(l => l.Id) },
            stableFixtures = new
            {
                referenceFixtureId = await db.Fixtures.Where(f => f.Id == 1393).Select(f => (int?)f.Id).FirstOrDefaultAsync(ct),
                gameweek = week?.ToRef(now),
                fixtureIds = week?.Fixtures.Select(f => f.Id) ?? [],
            },
            resetAtUtc = now,
        };
    }

    /// <summary>A fixed placeholder subject per reference manager when no staging Supabase user is configured (can't sign in).</summary>
    private static Guid DeterministicSubject(int index) => new($"00000000-0000-4000-8000-00000000000{index + 1}");
}
