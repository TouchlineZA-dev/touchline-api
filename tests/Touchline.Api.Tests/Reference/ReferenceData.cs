using System.Text.Json;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Reference;

/// <summary>
/// Loads tests/db/reference/gw9.json (a read-only production snapshot of GW9) and the worker's replay
/// pack into a test database, as the admin, with the worker's upsert semantics.
/// </summary>
public static class ReferenceData
{
    public static string Path(params string[] parts) => System.IO.Path.Combine([TestDatabase.RepoRoot(), "tests", "db", .. parts]);

    public static JsonElement Gw9() => JsonDocument.Parse(File.ReadAllText(Path("reference", "gw9.json"))).RootElement.Clone();

    /// <summary>Competition 806, season 2026/2027 (current), and returns the season id.</summary>
    public static Task<int> SeasonAsync(TestDb db) => db.ScalarAsync<int>("""
        WITH c AS (INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'Premier Division') RETURNING id)
        INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) SELECT 28123, id, '2026/2027', true FROM c RETURNING id
        """);

    /// <summary>Teams and players from the snapshot, with their production ids.</summary>
    public static async Task TeamsAndPlayersAsync(TestDb db, JsonElement gw9)
    {
        foreach (var t in gw9.GetProperty("teams").EnumerateArray())
        {
            await db.ExecAsync("INSERT INTO football.teams (id, sportmonks_team_id, name, short_code) VALUES ($1, $2, $3, $4)",
                t.GetProperty("id").GetInt32(), t.GetProperty("sportmonks_team_id").GetInt64(), t.GetProperty("name").GetString(), Str(t, "short_code"));
        }
        foreach (var p in gw9.GetProperty("players").EnumerateArray())
        {
            await db.ExecAsync("INSERT INTO football.players (id, sportmonks_player_id, display_name, common_name, position_id) VALUES ($1, $2, $3, $4, $5)",
                p.GetProperty("id").GetInt32(), p.GetProperty("sportmonks_player_id").GetInt64(), Str(p, "display_name"), Str(p, "common_name"), Short(p, "position_id"));
        }
    }

    /// <summary>All GW9 fixtures with their lineups and events, exactly as stored in production.</summary>
    public static async Task FixturesAsync(TestDb db, JsonElement gw9, int seasonId)
    {
        var competition = await db.ScalarAsync<int>("SELECT competition_id FROM football.seasons WHERE id = $1", seasonId);
        foreach (var f in gw9.GetProperty("fixtures").EnumerateArray())
        {
            await db.ExecAsync("""
                INSERT INTO football.fixtures (id, sportmonks_fixture_id, competition_id, season_id, state_id, home_team_id, away_team_id, home_score, away_score, starting_at, length)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """, f.GetProperty("id").GetInt32(), f.GetProperty("sportmonks_fixture_id").GetInt64(), competition, seasonId,
                Short(f, "state_id"), f.GetProperty("home_team_id").GetInt32(), f.GetProperty("away_team_id").GetInt32(),
                Short(f, "home_score"), Short(f, "away_score"), f.GetProperty("starting_at").GetDateTimeOffset().UtcDateTime, Short(f, "length"));
        }
        foreach (var l in gw9.GetProperty("lineups").EnumerateArray())
        {
            await db.ExecAsync("""
                INSERT INTO football.player_lineups (sportmonks_lineup_id, fixture_id, team_id, sportmonks_player_id, player_name, jersey_number, position_id, type_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """, l.GetProperty("sportmonks_lineup_id").GetInt64(), l.GetProperty("fixture_id").GetInt32(), Int(l, "team_id"), Long(l, "sportmonks_player_id"),
                Str(l, "player_name"), Short(l, "jersey_number"), Short(l, "position_id"), l.GetProperty("type_id").GetInt16());
        }
        foreach (var e in gw9.GetProperty("events").EnumerateArray())
        {
            await db.ExecAsync("""
                INSERT INTO football.match_events (sportmonks_event_id, fixture_id, team_id, sportmonks_player_id, sportmonks_related_player_id, player_name,
                                                   related_player_name, type_id, minute, extra_minute, result, sort_order, rescinded)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                """, e.GetProperty("sportmonks_event_id").GetInt64(), e.GetProperty("fixture_id").GetInt32(), Int(e, "team_id"), Long(e, "sportmonks_player_id"),
                Long(e, "sportmonks_related_player_id"), Str(e, "player_name"), Str(e, "related_player_name"), e.GetProperty("type_id").GetInt16(),
                Short(e, "minute"), Short(e, "extra_minute"), Str(e, "result"), Short(e, "sort_order"), Bool(e, "rescinded"));
        }
    }

    /// <summary>
    /// Applies one replay step to fixture 1393 the way the worker does: upsert by Sportmonks id, keep
    /// created_at, never delete. Participants 9960/2541 are teams 50 (Pirates) and 76 (Chiefs).
    /// </summary>
    public static async Task ApplyReplayStepAsync(TestDb db, string step, int seasonId)
    {
        var raw = JsonDocument.Parse(File.ReadAllText(Path("replay", "fixture_19781326", step + ".json"))).RootElement.GetProperty("data");
        var teams = new Dictionary<long, int> { [9960] = 50, [2541] = 76 };
        short? Score(string side) => raw.TryGetProperty("scores", out var scores)
            ? scores.EnumerateArray().Where(s => s.GetProperty("score").GetProperty("participant").GetString() == side)
                .Select(s => (short?)s.GetProperty("score").GetProperty("goals").GetInt16()).FirstOrDefault()
            : null;
        var competition = await db.ScalarAsync<int>("SELECT competition_id FROM football.seasons WHERE id = $1", seasonId);

        await db.ExecAsync("""
            INSERT INTO football.fixtures (id, sportmonks_fixture_id, competition_id, season_id, state_id, home_team_id, away_team_id, home_score, away_score, starting_at, length)
            VALUES (1393, $1, $2, $3, $4, 50, 76, $5, $6, $7, $8)
            ON CONFLICT (sportmonks_fixture_id) DO UPDATE SET state_id = EXCLUDED.state_id, home_score = EXCLUDED.home_score, away_score = EXCLUDED.away_score, updated_at = now()
            """, raw.GetProperty("id").GetInt64(), competition, seasonId, raw.GetProperty("state_id").GetInt16(), Score("home"), Score("away"),
            DateTime.SpecifyKind(DateTime.Parse(raw.GetProperty("starting_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc),
            raw.GetProperty("length").GetInt16());

        foreach (var l in raw.GetProperty("lineups").EnumerateArray())
        {
            await db.ExecAsync("""
                INSERT INTO football.player_lineups (sportmonks_lineup_id, fixture_id, team_id, sportmonks_player_id, player_name, jersey_number, position_id, type_id)
                VALUES ($1, 1393, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (sportmonks_lineup_id) DO UPDATE SET type_id = EXCLUDED.type_id, sportmonks_player_id = EXCLUDED.sportmonks_player_id, updated_at = now()
                """, l.GetProperty("id").GetInt64(), teams[l.GetProperty("team_id").GetInt64()], Long(l, "player_id"), Str(l, "player_name"),
                Short(l, "jersey_number"), Short(l, "position_id"), l.GetProperty("type_id").GetInt16());
        }
        foreach (var e in raw.TryGetProperty("events", out var events) ? events.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            await db.ExecAsync("""
                INSERT INTO football.match_events (sportmonks_event_id, fixture_id, team_id, sportmonks_player_id, sportmonks_related_player_id, player_name,
                                                   related_player_name, type_id, minute, extra_minute, result, rescinded)
                VALUES ($1, 1393, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (sportmonks_event_id) DO UPDATE SET sportmonks_player_id = EXCLUDED.sportmonks_player_id,
                    sportmonks_related_player_id = EXCLUDED.sportmonks_related_player_id, player_name = EXCLUDED.player_name,
                    related_player_name = EXCLUDED.related_player_name, minute = EXCLUDED.minute, rescinded = EXCLUDED.rescinded, updated_at = now()
                """, e.GetProperty("id").GetInt64(), teams[e.GetProperty("participant_id").GetInt64()], Long(e, "player_id"), Long(e, "related_player_id"),
                Str(e, "player_name"), Str(e, "related_player_name"), e.GetProperty("type_id").GetInt16(), Short(e, "minute"), Short(e, "extra_minute"),
                Str(e, "result"), Bool(e, "rescinded"));
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static short? Short(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt16() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
