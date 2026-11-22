using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Touchline.Api.Models;

namespace Touchline.Api.Data;

/// <summary>
/// One context over the shared Supabase Postgres database.
/// - football, sync: read-only (owned by the sync worker; the DB role cannot write them).
/// - app: read/write (owned by this API).
/// The schema is created by Flyway in the touchline-sync repo. This context does NOT
/// create or migrate tables — do not run EF migrations against the shared database.
/// </summary>
public class TouchlineDbContext(DbContextOptions<TouchlineDbContext> options) : DbContext(options)
{
    // football (read-only)
    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Fixture> Fixtures => Set<Fixture>();
    public DbSet<MatchEvent> MatchEvents => Set<MatchEvent>();
    public DbSet<PlayerLineup> PlayerLineups => Set<PlayerLineup>();
    public DbSet<TeamMatchStat> TeamMatchStats => Set<TeamMatchStat>();

    // sync (read-only)
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<GameweekSyncStatus> GameweekSyncStatuses => Set<GameweekSyncStatus>();

    // app (read/write)
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Gameweek> Gameweeks => Set<Gameweek>();
    public DbSet<FantasyTeam> FantasyTeams => Set<FantasyTeam>();
    public DbSet<FantasySquadPick> FantasySquads => Set<FantasySquadPick>();
    public DbSet<PointsResult> PointsResults => Set<PointsResult>();
    public DbSet<LeaderboardEntry> Leaderboards => Set<LeaderboardEntry>();
    public DbSet<PlayerPrice> PlayerPrices => Set<PlayerPrice>();
    public DbSet<PlayerPriceChange> PlayerPriceChanges => Set<PlayerPriceChange>();

    // app, added by touchline-sync V035 (read/write)
    public DbSet<FantasyGameweekEntry> GameweekEntries => Set<FantasyGameweekEntry>();
    public DbSet<FantasyTransfer> Transfers => Set<FantasyTransfer>();
    public DbSet<FantasyChipUsage> ChipUsage => Set<FantasyChipUsage>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<LeagueMember> LeagueMembers => Set<LeagueMember>();
    public DbSet<TransferWindow> TransferWindows => Set<TransferWindow>();
    public DbSet<PlayerPriceOverride> PriceOverrides => Set<PlayerPriceOverride>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // football
        b.Entity<Competition>().ToTable("competitions", "football");
        b.Entity<Season>().ToTable("seasons", "football");
        b.Entity<Team>().ToTable("teams", "football");
        b.Entity<Player>().ToTable("players", "football");
        b.Entity<Fixture>().ToTable("fixtures", "football");
        b.Entity<MatchEvent>().ToTable("match_events", "football");
        b.Entity<PlayerLineup>().ToTable("player_lineups", "football");
        b.Entity<TeamMatchStat>().ToTable("player_match_stats", "football");

        // sync
        b.Entity<SyncJob>().ToTable("sync_jobs", "sync");
        b.Entity<SyncLog>().ToTable("sync_logs", "sync");
        b.Entity<GameweekSyncStatus>().ToTable("gameweek_sync_status", "sync");

        // app
        b.Entity<AppUser>().ToTable("users", "app");
        b.Entity<Gameweek>().ToTable("gameweeks", "app");
        b.Entity<FantasyTeam>().ToTable("fantasy_teams", "app");
        b.Entity<FantasySquadPick>().ToTable("fantasy_squads", "app");
        b.Entity<PointsResult>().ToTable("points_results", "app");
        b.Entity<LeaderboardEntry>().ToTable("leaderboards", "app");
        b.Entity<PlayerPrice>().ToTable("player_prices", "app");
        b.Entity<PlayerPriceChange>().ToTable("player_price_changes", "app");
        b.Entity<FantasyGameweekEntry>().ToTable("fantasy_gameweek_entries", "app");
        b.Entity<FantasyTransfer>().ToTable("fantasy_transfers", "app");
        b.Entity<FantasyChipUsage>().ToTable("fantasy_chip_usage", "app");
        b.Entity<League>().ToTable("leagues", "app");
        b.Entity<LeagueMember>().ToTable("league_members", "app");
        b.Entity<TransferWindow>().ToTable("transfer_windows", "app");
        b.Entity<PlayerPriceOverride>().ToTable("player_price_overrides", "app");

        // NUMERIC(5,1) money columns
        b.Entity<FantasyTeam>().Property(x => x.BudgetRemaining).HasPrecision(5, 1);
        b.Entity<FantasySquadPick>().Property(x => x.PurchasePrice).HasPrecision(5, 1);
        b.Entity<PlayerPrice>().Property(x => x.Price).HasPrecision(5, 1);
        b.Entity<PlayerPriceChange>().Property(x => x.OldPrice).HasPrecision(5, 1);
        b.Entity<PlayerPriceChange>().Property(x => x.NewPrice).HasPrecision(5, 1);
        b.Entity<FantasyTransfer>().Property(x => x.PriceOut).HasPrecision(5, 1);
        b.Entity<FantasyTransfer>().Property(x => x.PriceIn).HasPrecision(5, 1);
        b.Entity<PlayerPriceOverride>().Property(x => x.Price).HasPrecision(5, 1);

        // joined_at defaults to now() in the database, like created_at.
        b.Entity<LeagueMember>().Property(x => x.JoinedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        b.Entity<PointsResult>().Property(x => x.Breakdown).HasColumnType("jsonb");

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));

                // created_at / updated_at are set by DB defaults and the set_updated_at()
                // trigger (V002). Never send them from code.
                if (property.Name == "CreatedAt")
                {
                    property.ValueGenerated = ValueGenerated.OnAdd;
                    property.SetDefaultValueSql("now()");
                }
                else if (property.Name == "UpdatedAt")
                {
                    property.ValueGenerated = ValueGenerated.OnAddOrUpdate;
                    property.SetDefaultValueSql("now()");
                }
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardReadOnlySchemas();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardReadOnlySchemas();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fail fast in code before the database rejects a write to football/sync.</summary>
    private void GuardReadOnlySchemas()
    {
        var offending = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Metadata.GetSchema())
            .FirstOrDefault(schema => schema is "football" or "sync");

        if (offending is not null)
        {
            throw new InvalidOperationException(
                $"The '{offending}' schema is owned by the sync worker and is read-only for the API.");
        }
    }

    private static string ToSnakeCase(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
