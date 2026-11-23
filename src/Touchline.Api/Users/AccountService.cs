using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;
using Touchline.Api.Models;
using Touchline.Api.Services;

namespace Touchline.Api.Users;

public sealed record OnboardingView(bool NeedsUsername, bool NeedsTeam, bool NeedsSquad);

public sealed record AccountPoliciesView(
    int UsernameMinLength, int UsernameMaxLength, string UsernameCharacters, int UsernameCooldownDays,
    DateTimeOffset? UsernameChangeAllowedFromUtc, string Moderation, string Recovery, int RecentAuthMinutes);

public sealed record MeView(
    int Id,
    string Username,
    string? Email,
    Guid AuthUserId,
    OnboardingView Onboarding,
    int? FavouriteClubId,
    bool Suspended,
    AccountPoliciesView Policies);

/// <param name="Username">New public username, or null to leave it.</param>
/// <param name="FavouriteClubId">football.teams.id of a current-season PSL club; send clearFavouriteClub to remove it.</param>
public sealed record UpdateMeRequest(string? Username = null, int? FavouriteClubId = null, bool ClearFavouriteClub = false);

public sealed record NotificationPreferencesView(bool GoalAlerts, bool DeadlineReminders, bool LeagueUpdates, DateTimeOffset? UpdatedAtUtc);

public sealed record UpdateNotificationsRequest(bool? GoalAlerts = null, bool? DeadlineReminders = null, bool? LeagueUpdates = null);

public sealed record SuspendRequest(bool Suspended, string Reason);

internal sealed class AccountRow
{
    public string? UsernameCanonical { get; set; }
    public DateTimeOffset? UsernameChangedAt { get; set; }
    public int? FavouriteClubId { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

internal sealed class PreferenceRow
{
    public bool GoalAlerts { get; set; }
    public bool DeadlineReminders { get; set; }
    public bool LeagueUpdates { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The manager's account (03-Leruo-API Stage 3, handbook Gate 5): username, favourite club, onboarding
/// state, notification preferences, export, deletion (anonymisation) and suspension. Everything that
/// needs docs/migrations/009 checks for it first; GET /me works without it.
/// </summary>
public sealed class AccountService(
    TouchlineDbContext db,
    SchemaFeatures schema,
    FootballService football,
    CurrentUser currentUser,
    IOptions<AccountOptions> options,
    TimeProvider time)
{
    private readonly AccountOptions _options = options.Value;

    public async Task<MeView> MeAsync(AppUser user, CancellationToken ct)
    {
        var account = await AccountAsync(user.Id, ct);
        var season = await football.GetCurrentPslSeasonAsync(ct);
        var teamId = season is null ? null : await db.FantasyTeams
            .Where(t => t.UserId == user.Id && t.SeasonId == season.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
        var hasSquad = teamId is not null && await db.FantasySquads.AnyAsync(p => p.FantasyTeamId == teamId, ct);
        var needsUsername = account is not null ? account.UsernameCanonical is null : UsernameRules.IsGenerated(user.Username);

        return new MeView(
            user.Id, user.Username, RealEmail(user.Email), user.AuthUserId!.Value,
            new OnboardingView(needsUsername, teamId is null, teamId is not null && !hasSquad),
            account?.FavouriteClubId,
            account?.SuspendedAt is not null,
            Policies(account));
    }

    /// <summary>PATCH /me: username and/or favourite club.</summary>
    public async Task<MeView> UpdateAsync(AppUser user, UpdateMeRequest request, CancellationToken ct)
    {
        var account = await RequireAccountColumnsAsync(user.Id, ct);
        await RequireNotSuspendedAsync(account);
        var now = time.GetUtcNow();

        if (request.Username is not null)
        {
            var errors = UsernameRules.Validate(request.Username);
            if (errors.Count > 0) throw new RuleViolationException(errors, "Choose another username");
            var name = UsernameRules.Normalise(request.Username);
            var canonical = UsernameRules.Canonical(name);

            if (canonical != account.UsernameCanonical || name != user.Username)
            {
                if (account.UsernameCanonical is not null && account.UsernameChangedAt is { } last
                    && now < last.AddDays(_options.UsernameCooldownDays) && canonical != account.UsernameCanonical)
                {
                    throw new RuleViolationException(
                        [$"You can change your username again from {last.AddDays(_options.UsernameCooldownDays):yyyy-MM-dd HH:mm} UTC."],
                        "Username recently changed");
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                try
                {
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE app.users SET username = {name}, username_canonical = {canonical}, username_changed_at = {now}
                        WHERE id = {user.Id}
                        """, ct);
                }
                catch (Exception ex) when (FantasyContextService.IsUniqueViolation(ex))
                {
                    // Another manager holds it (or won a simultaneous claim). Never say who.
                    throw new CodedException(StatusCodes.Status409Conflict, CodedException.UsernameTaken, "Choose another username",
                        "That username is taken.");
                }
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO app.username_history (user_id, old_username, new_username) VALUES ({user.Id}, {user.Username}, {name})
                    """, ct);
                await transaction.CommitAsync(ct);
                user.Username = name;
            }
        }

        if (request.ClearFavouriteClub)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE app.users SET favourite_club_id = NULL WHERE id = {user.Id}", ct);
        }
        else if (request.FavouriteClubId is { } clubId)
        {
            var season = await football.GetCurrentPslSeasonAsync(ct)
                ?? throw new NotFoundException("No current PSL season is flagged is_current in football.seasons.");
            var clubs = await football.GetTeamsAsync(season.Id, ct);
            if (clubs.All(c => c.Id != clubId))
            {
                throw new RuleViolationException($"Club {clubId} isn't playing in the current PSL season. GET /api/v1/clubs lists them.");
            }
            await db.Database.ExecuteSqlAsync($"UPDATE app.users SET favourite_club_id = {clubId} WHERE id = {user.Id}", ct);
        }

        return await MeAsync(user, ct);
    }

    public async Task<NotificationPreferencesView> NotificationsAsync(AppUser user, CancellationToken ct)
    {
        if (!await schema.HasTableAsync("app", "notification_preferences", ct)) return new(true, true, true, null);
        var row = await db.Database.SqlQuery<PreferenceRow>($"""
            SELECT goal_alerts AS "GoalAlerts", deadline_reminders AS "DeadlineReminders", league_updates AS "LeagueUpdates", updated_at AS "UpdatedAt"
            FROM app.notification_preferences WHERE user_id = {user.Id}
            """).FirstOrDefaultAsync(ct);
        return row is null ? new(true, true, true, null) : new(row.GoalAlerts, row.DeadlineReminders, row.LeagueUpdates, row.UpdatedAt);
    }

    public async Task<NotificationPreferencesView> UpdateNotificationsAsync(AppUser user, UpdateNotificationsRequest request, CancellationToken ct)
    {
        await RequireNotSuspendedAsync(await RequireAccountColumnsAsync(user.Id, ct));
        var current = await NotificationsAsync(user, ct);
        bool goal = request.GoalAlerts ?? current.GoalAlerts, deadline = request.DeadlineReminders ?? current.DeadlineReminders,
             league = request.LeagueUpdates ?? current.LeagueUpdates;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO app.notification_preferences (user_id, goal_alerts, deadline_reminders, league_updates, updated_at)
            VALUES ({user.Id}, {goal}, {deadline}, {league}, now())
            ON CONFLICT (user_id) DO UPDATE SET goal_alerts = EXCLUDED.goal_alerts, deadline_reminders = EXCLUDED.deadline_reminders,
                league_updates = EXCLUDED.league_updates, updated_at = now()
            """, ct);
        return await NotificationsAsync(user, ct);
    }

    /// <summary>GET /me/export: everything stored about the manager, as JSON. Needs a recent sign-in.</summary>
    public async Task<object> ExportAsync(AppUser user, CancellationToken ct)
    {
        RequireRecentSignIn();
        var account = await AccountAsync(user.Id, ct);
        var teams = await db.FantasyTeams.Where(t => t.UserId == user.Id).ToListAsync(ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var hasHistory = await schema.HasTableAsync("app", "username_history", ct);

        return new
        {
            exportedAtUtc = time.GetUtcNow(),
            profile = new
            {
                user.Id, user.Username, email = RealEmail(user.Email), authUserId = user.AuthUserId, user.CreatedAt,
                favouriteClubId = account?.FavouriteClubId, suspended = account?.SuspendedAt is not null,
            },
            notificationPreferences = await NotificationsAsync(user, ct),
            usernameHistory = hasHistory
                ? await db.Database.SqlQuery<HistoryRow>($"""
                    SELECT old_username AS "Old", new_username AS "New", changed_at AS "ChangedAt" FROM app.username_history WHERE user_id = {user.Id} ORDER BY id
                    """).ToListAsync(ct)
                : new List<HistoryRow>(),
            teams = teams.Select(t => new { t.Id, t.Name, t.SeasonId, t.BudgetRemaining, t.CreatedAt }),
            teamNameHistory = hasHistory && teamIds.Count > 0
                ? await db.Database.SqlQuery<HistoryRow>($"""
                    SELECT old_name AS "Old", new_name AS "New", changed_at AS "ChangedAt" FROM app.fantasy_team_name_history
                    WHERE fantasy_team_id = ANY({teamIds.ToArray()}) ORDER BY id
                    """).ToListAsync(ct)
                : new List<HistoryRow>(),
            squads = await db.FantasySquads.Where(p => teamIds.Contains(p.FantasyTeamId))
                .OrderBy(p => p.GameweekId).ThenBy(p => p.Id)
                .Select(p => new { p.FantasyTeamId, p.GameweekId, p.PlayerId, p.IsCaptain, p.IsViceCaptain, p.IsOnBench, p.BenchOrder, p.PurchasePrice })
                .ToListAsync(ct),
            gameweekEntries = await db.GameweekEntries.Where(e => teamIds.Contains(e.FantasyTeamId))
                .OrderBy(e => e.GameweekId)
                .Select(e => new { e.FantasyTeamId, e.GameweekId, e.Formation, e.Chip, e.TransfersMade, e.TransferCost, e.Points, e.ScoredAt })
                .ToListAsync(ct),
            transfers = await db.Transfers.Where(x => teamIds.Contains(x.FantasyTeamId)).OrderBy(x => x.Id)
                .Select(x => new { x.FantasyTeamId, x.GameweekId, x.PlayerOutId, x.PlayerInId, x.PriceOut, x.PriceIn, x.CreatedAt }).ToListAsync(ct),
            chips = await db.ChipUsage.Where(c => teamIds.Contains(c.FantasyTeamId))
                .Select(c => new { c.FantasyTeamId, c.Chip, c.GameweekId, c.CreatedAt }).ToListAsync(ct),
            leagueMemberships = await db.LeagueMembers.Where(m => teamIds.Contains(m.FantasyTeamId))
                .Select(m => new { m.LeagueId, m.FantasyTeamId, m.JoinedAt, m.LeftAt }).ToListAsync(ct),
            leaguesOwned = await db.Leagues.Where(l => l.OwnerUserId == user.Id)
                .Select(l => new { l.Id, l.Name, l.JoinCode, l.IsActive, l.CreatedAt }).ToListAsync(ct),
        };
    }

    /// <summary>
    /// DELETE /me: anonymise, never delete rows (SPRINT_RULES rule 3). Needs a recent sign-in.
    /// - username becomes deleted_{id}, email is cleared, favourite club and preferences are removed;
    /// - the manager's teams become "Deleted manager {teamId}", keeping their ids, history and scores;
    /// - each league the manager owns passes to the oldest remaining member, or is archived if none;
    /// - the row keeps auth_user_id, so the old sign-in gets 403 account_deleted, not a new account.
    /// The Supabase Auth user itself is removed by an operator (docs/accounts/policies.md).
    /// </summary>
    public async Task<object> DeleteAsync(AppUser user, CancellationToken ct)
    {
        RequireRecentSignIn();
        await RequireAccountColumnsAsync(user.Id, ct);
        var now = time.GetUtcNow();
        var anonymous = $"deleted_{user.Id}";
        var placeholder = await schema.IsNullableAsync("app", "users", "email", ct) ? null : $"deleted-{user.Id}@no-email.touchline.invalid";

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE app.users SET username = {anonymous}, username_canonical = {anonymous}, username_changed_at = {now},
                email = {placeholder}, favourite_club_id = NULL, is_active = false, deleted_at = {now}
            WHERE id = {user.Id}
            """, ct);
        await db.Database.ExecuteSqlAsync($"INSERT INTO app.username_history (user_id, old_username, new_username) VALUES ({user.Id}, {user.Username}, {anonymous})", ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE app.notification_preferences SET goal_alerts = false, deadline_reminders = false, league_updates = false, updated_at = now()
            WHERE user_id = {user.Id}
            """, ct);

        var teams = await db.FantasyTeams.AsTracking().Where(t => t.UserId == user.Id).ToListAsync(ct);
        foreach (var team in teams)
        {
            var newName = $"Deleted manager {team.Id}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO app.fantasy_team_name_history (fantasy_team_id, old_name, new_name, changed_by_user_id) VALUES ({team.Id}, {team.Name}, {newName}, {user.Id})
                """, ct);
            team.Name = newName;
        }

        var transferred = new List<object>();
        var archived = new List<int>();
        var owned = await db.Leagues.AsTracking().Where(l => l.OwnerUserId == user.Id && l.IsActive).ToListAsync(ct);
        foreach (var league in owned)
        {
            var heir = await (from m in db.LeagueMembers
                              join t in db.FantasyTeams on m.FantasyTeamId equals t.Id
                              join u in db.Users on t.UserId equals u.Id
                              where m.LeagueId == league.Id && m.LeftAt == null && t.UserId != user.Id && u.IsActive
                              orderby m.JoinedAt, m.Id
                              select (int?)t.UserId).FirstOrDefaultAsync(ct);
            if (heir is { } newOwner)
            {
                league.OwnerUserId = newOwner;
                transferred.Add(new { leagueId = league.Id, newOwnerUserId = newOwner });
            }
            else
            {
                league.IsActive = false;
                archived.Add(league.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new
        {
            deleted = true,
            deletedAtUtc = now,
            username = anonymous,
            teamsAnonymised = teams.Count,
            leaguesTransferred = transferred,
            leaguesArchived = archived,
            next = "Your sign-in will be removed from Supabase Auth by support. Historical scores stay, without your name.",
        };
    }

    /// <summary>Admin: suspend (read-only, hidden from the Overall league) or reinstate an account.</summary>
    public async Task<object> SetSuspendedAsync(int userId, SuspendRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RuleViolationException("Every suspension change needs a reason.");
        await RequireAccountColumnsAsync(userId, ct);
        if (request.Suspended)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE app.users SET suspended_at = now(), suspension_reason = {request.Reason.Trim()} WHERE id = {userId}", ct);
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"UPDATE app.users SET suspended_at = NULL, suspension_reason = NULL WHERE id = {userId}", ct);
        }
        return new { userId, suspended = request.Suspended };
    }

    /// <summary>Writes are refused for a suspended account (reads still work). A no-op before 009.</summary>
    public async Task RequireCanWriteAsync(int userId, CancellationToken ct)
    {
        var account = await AccountAsync(userId, ct);
        if (account is not null) await RequireNotSuspendedAsync(account);
    }

    /// <summary>Teams whose managers are suspended: hidden from the Overall league. Empty before 009.</summary>
    public async Task<HashSet<int>> SuspendedTeamIdsAsync(int seasonId, CancellationToken ct)
    {
        if (!await schema.HasColumnAsync("app", "users", "suspended_at", ct)) return [];
        return (await db.Database.SqlQuery<int>($"""
            SELECT t.id AS "Value" FROM app.fantasy_teams t JOIN app.users u ON u.id = t.user_id
            WHERE t.season_id = {seasonId} AND u.suspended_at IS NOT NULL
            """).ToListAsync(ct)).ToHashSet();
    }

    private void RequireRecentSignIn()
    {
        var issued = currentUser.IssuedAt;
        if (issued is null || time.GetUtcNow() - issued.Value > TimeSpan.FromMinutes(_options.RecentAuthMinutes))
        {
            throw new CodedException(StatusCodes.Status403Forbidden, CodedException.ReauthenticationRequired, "Sign in again",
                $"For your security, sign in again (within the last {_options.RecentAuthMinutes} minutes) before doing this.");
        }
    }

    private static Task RequireNotSuspendedAsync(AccountRow account) =>
        account.SuspendedAt is not null
            ? throw new CodedException(StatusCodes.Status403Forbidden, CodedException.AccountSuspended, "Account suspended",
                "This account is suspended: you can read but not make changes. Contact support.")
            : Task.CompletedTask;

    private async Task<AccountRow> RequireAccountColumnsAsync(int userId, CancellationToken ct) =>
        await AccountAsync(userId, ct) ?? throw new SchemaNotReadyException(
            "Account changes need docs/migrations/009_accounts.sql (touchline-sync V041).");

    private async Task<AccountRow?> AccountAsync(int userId, CancellationToken ct)
    {
        if (!await schema.HasColumnAsync("app", "users", "username_canonical", ct)) return null;
        return await db.Database.SqlQuery<AccountRow>($"""
            SELECT username_canonical AS "UsernameCanonical", username_changed_at AS "UsernameChangedAt", favourite_club_id AS "FavouriteClubId",
                   suspended_at AS "SuspendedAt", deleted_at AS "DeletedAt"
            FROM app.users WHERE id = {userId}
            """).SingleAsync(ct);
    }

    private AccountPoliciesView Policies(AccountRow? account) => new(
        UsernameRules.MinLength, UsernameRules.MaxLength, "letters, digits and underscore (after Unicode NFKC normalisation)",
        _options.UsernameCooldownDays,
        account?.UsernameCanonical is not null && account.UsernameChangedAt is { } changed ? changed.AddDays(_options.UsernameCooldownDays) : null,
        "Usernames and team names are public. Offensive names are renamed by an admin; repeated abuse leads to suspension. Rename history is kept for moderation.",
        "Lost access: sign in with any provider linked to your Supabase account. Unlinked older accounts and email conflicts are linked by support (docs/accounts/policies.md).",
        _options.RecentAuthMinutes);

    /// <summary>The placeholder used before 009 for accounts without email is never shown.</summary>
    private static string? RealEmail(string? email) =>
        email is null || email.EndsWith(".touchline.invalid", StringComparison.OrdinalIgnoreCase) ? null : email;

    internal sealed class HistoryRow
    {
        public string Old { get; set; } = "";
        public string New { get; set; } = "";
        public DateTimeOffset ChangedAt { get; set; }
    }
}
