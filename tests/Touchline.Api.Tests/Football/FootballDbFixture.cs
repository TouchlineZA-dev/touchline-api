using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Football;

/// <summary>
/// A full canonical database with a current PSL season, two clubs and their players. Football rows
/// are written as the admin, the way the sync worker writes them; the API reads as app_api_user.
/// Each test seeds its own fixtures with fresh ids, so tests share the database safely.
/// </summary>
public sealed class FootballDbFixture : IAsyncLifetime
{
    /// <summary>The test clock starts at real time (to the second), so Supabase-style tokens issued now are "recent".</summary>
    public static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public string? SkipReason { get; private set; }
    public TestDb Db { get; private set; } = null!;
    public TestTokens Tokens { get; } = new();
    public TestClock Clock { get; } = new(Start);
    public TestApiFactory Api { get; private set; } = null!;

    public int CompetitionId { get; private set; }
    public int SeasonId { get; private set; }
    public int Home { get; private set; }
    public int Away { get; private set; }

    private static int _next = 1000;
    public static int NextId() => Interlocked.Increment(ref _next);

    public async Task InitializeAsync()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        if (server is null)
        {
            SkipReason = skip;
            return;
        }

        Db = await TestDatabase.CreateAsync(server, "football");
        CompetitionId = await Db.ScalarAsync<int>(
            "INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'Premier Division') RETURNING id");
        SeasonId = await Db.ScalarAsync<int>(
            "INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) VALUES (28123, $1, '2026/2027', true) RETURNING id",
            CompetitionId);
        Home = await TeamAsync("Orlando Pirates", "ORL");
        Away = await TeamAsync("Kaizer Chiefs", "KC");
        Api = new TestApiFactory(Db.ApiConnectionString, Tokens, Clock);
    }

    public Task DisposeAsync()
    {
        Api?.Dispose();
        return Task.CompletedTask;
    }

    public Task<int> TeamAsync(string name, string code) => Db.ScalarAsync<int>(
        "INSERT INTO football.teams (sportmonks_team_id, name, short_code, image_path) VALUES ($1, $2, $3, 'https://cdn.sportmonks.com/images/soccer/team_placeholder.png') RETURNING id",
        NextId(), name, code);

    /// <summary>A player; returns (internal id, Sportmonks id). Position 24 GK, 25 DEF, 26 MID, 27 ATT.</summary>
    public async Task<(int Id, long SportmonksId)> PlayerAsync(string name, short positionId = 27)
    {
        long smId = NextId() + 500000;
        var id = await Db.ScalarAsync<int>(
            "INSERT INTO football.players (sportmonks_player_id, display_name, position_id) VALUES ($1, $2, $3) RETURNING id",
            smId, name, positionId);
        return (id, smId);
    }

    public Task<int> FixtureAsync(DateTimeOffset kickoff, short state, short? homeScore = null, short? awayScore = null, int? home = null, int? away = null) =>
        Db.ScalarAsync<int>("""
            INSERT INTO football.fixtures (sportmonks_fixture_id, competition_id, season_id, state_id, home_team_id, away_team_id,
                                           home_score, away_score, starting_at, length)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 90) RETURNING id
            """, NextId() + 19000000, CompetitionId, SeasonId, state, home ?? Home, away ?? Away, homeScore, awayScore, kickoff.UtcDateTime);

    public Task LineupAsync(int fixtureId, int teamId, long? sportmonksPlayerId, short type, string name = "Player", short? jersey = null) => Db.ExecAsync("""
        INSERT INTO football.player_lineups (sportmonks_lineup_id, fixture_id, team_id, sportmonks_player_id, player_name, jersey_number, type_id)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
        """, (long)NextId() + 900000000, fixtureId, teamId, sportmonksPlayerId, name, jersey, type);

    /// <summary>Inserts or updates (by Sportmonks event id) one event, like the worker's upsert.</summary>
    public Task EventAsync(int fixtureId, long sportmonksEventId, short type, int teamId, long? player, long? related, short minute,
        short? extra = null, bool? rescinded = null) => Db.ExecAsync("""
        INSERT INTO football.match_events (sportmonks_event_id, fixture_id, team_id, sportmonks_player_id, sportmonks_related_player_id,
                                           player_name, type_id, minute, extra_minute, rescinded)
        VALUES ($1, $2, $3, $4, $5, 'Provider name', $6, $7, $8, $9)
        ON CONFLICT (sportmonks_event_id) DO UPDATE SET
            sportmonks_player_id = EXCLUDED.sportmonks_player_id,
            sportmonks_related_player_id = EXCLUDED.sportmonks_related_player_id,
            type_id = EXCLUDED.type_id, minute = EXCLUDED.minute, extra_minute = EXCLUDED.extra_minute,
            rescinded = EXCLUDED.rescinded, updated_at = now()
        """, sportmonksEventId, fixtureId, teamId, player, related, type, minute, extra, rescinded);

    public Task SetFixtureAsync(int fixtureId, short state, short? homeScore, short? awayScore) => Db.ExecAsync(
        "UPDATE football.fixtures SET state_id = $2, home_score = $3, away_score = $4 WHERE id = $1", fixtureId, state, homeScore, awayScore);

    /// <summary>Pretends the worker last wrote this fixture's rows at <paramref name="at"/>.</summary>
    public async Task BackdateAsync(int fixtureId, DateTimeOffset at)
    {
        // The set_updated_at() triggers would overwrite updated_at, so they're switched off for this session.
        var ts = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var sql = "SET session_replication_role = replica;";
        foreach (var table in new[] { "fixtures", "match_events", "player_lineups", "player_match_stats" })
        {
            var column = table == "fixtures" ? "id" : "fixture_id";
            sql += $"UPDATE football.{table} SET created_at = '{ts}+00', updated_at = '{ts}+00' WHERE {column} = {fixtureId};";
        }
        await Db.ExecAsync(sql + "SET session_replication_role = origin;");
    }

    public Task<int> GameweekAsync(string name, DateOnly start, DateOnly end, DateTimeOffset deadline) => Db.ScalarAsync<int>("""
        INSERT INTO app.gameweeks (name, start_date, end_date, season_id, deadline_at, number)
        VALUES ($1, $2, $3, $4, $5, (SELECT COALESCE(MAX(number), 0) + 1 FROM app.gameweeks WHERE season_id = $4)) RETURNING id
        """, name, start, end, SeasonId, deadline.UtcDateTime);
}
