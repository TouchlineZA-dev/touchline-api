using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;

namespace Touchline.Api.Tests.Audit;

/// <summary>
/// The functional audit's world (docs/audits/api-functional-audit-2026-11-25.md), seeded on a disposable
/// database built from the pinned Flyway migrations: a current PSL season, 16 clubs with 15 priced players
/// each (2 GK, 5 DEF, 5 MID, 3 ATT) who all appeared in a current-season lineup, 4 players with no lineup
/// (ineligible), and four gameweeks: a locked past one, the open next one (deadline in 3 days) and two later.
///
/// Prices are the same at every club, so squads with exact costs are easy to build:
/// GK 4.0 / 4.5, DEF 4.0 4.5 5.0 5.5 6.5, MID 4.5 5.5 6.5 8.0 9.5, ATT 5.5 7.5 10.5.
/// Keys look like "c3-def2" (club 3, second defender) and "x-mid" (ineligible midfielder).
/// </summary>
public static class AuditWorld
{
    public sealed record Pool(
        IReadOnlyList<int> Clubs,
        IReadOnlyDictionary<string, int> P,
        IReadOnlyDictionary<string, long> Sm,
        IReadOnlyDictionary<string, decimal> Price,
        int PastGameweek,
        int NextGameweek,
        int LaterGameweek,
        int FourthGameweek,
        DateTimeOffset NextDeadline,
        DateTimeOffset LaterDeadline,
        DateTimeOffset FourthDeadline)
    {
        public int this[string key] => P[key];
    }

    public static readonly (string Code, short Position, decimal[] Prices)[] Roster =
    [
        ("gk", 24, [4.0m, 4.5m]),
        ("def", 25, [4.0m, 4.5m, 5.0m, 5.5m, 6.5m]),
        ("mid", 26, [4.5m, 5.5m, 6.5m, 8.0m, 9.5m]),
        ("att", 27, [5.5m, 7.5m, 10.5m]),
    ];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Pool> Pools = [];

    public static async Task<Pool> EnsureAsync(FootballDbFixture f)
    {
        await Gate.WaitAsync();
        try
        {
            if (Pools.TryGetValue(f.Db.Name, out var existing)) return existing;
            var now = f.Clock.Now;
            var clubs = new List<int>();
            for (var c = 0; c < 16; c++) clubs.Add(await f.TeamAsync($"Audit Club {c:00}", $"A{c:00}"));

            // One finished fixture per pair of clubs, 30 days ago: every rostered player is in its lineup.
            var fixtures = new List<int>();
            for (var c = 0; c < 16; c += 2) fixtures.Add(await f.FixtureAsync(now.AddDays(-30 + c / 2.0), 5, 1, 0, clubs[c], clubs[c + 1]));

            var rows = new List<(string Key, long Sm, string Name, short Position, decimal Price, int? Club, int? Fixture)>();
            var smBase = 7_000_000L + Math.Abs(f.Db.Name.GetHashCode() % 1000) * 1000;
            var n = 0;
            for (var c = 0; c < 16; c++)
            {
                foreach (var (code, position, prices) in Roster)
                {
                    for (var i = 0; i < prices.Length; i++)
                    {
                        rows.Add(($"c{c}-{code}{i + 1}", smBase + n++, $"A{c:00} {code.ToUpperInvariant()}{i + 1}", position, prices[i], clubs[c], fixtures[c / 2]));
                    }
                }
            }
            foreach (var (code, position, _) in Roster) rows.Add(($"x-{code}", smBase + n++, $"Unsigned {code.ToUpperInvariant()}", position, 4.0m, null, null));

            var values = string.Join(",\n", rows.Select(r => $"({r.Sm}, '{r.Name}', {r.Position})"));
            await f.Db.ExecAsync($"INSERT INTO football.players (sportmonks_player_id, display_name, position_id) VALUES {values}");
            var ids = await QueryAsync(f.Db, $"SELECT sportmonks_player_id, id FROM football.players WHERE sportmonks_player_id BETWEEN {smBase} AND {smBase + n}",
                r => (r.GetInt64(0), r.GetInt32(1)));
            var bySm = ids.ToDictionary(x => x.Item1, x => x.Item2);

            var lineupRows = rows.Where(r => r.Club is not null).Select((r, i) =>
                $"({smBase * 10 + i}, {r.Fixture}, {r.Club}, {r.Sm}, '{r.Name}', 11)");
            await f.Db.ExecAsync("INSERT INTO football.player_lineups (sportmonks_lineup_id, fixture_id, team_id, sportmonks_player_id, player_name, type_id) VALUES "
                + string.Join(",\n", lineupRows));
            var priceRows = rows.Select(r => $"({bySm[r.Sm]}, {f.SeasonId}, {r.Price.ToString(CultureInfo.InvariantCulture)})");
            await f.Db.ExecAsync("INSERT INTO app.player_prices (player_id, season_id, price) VALUES " + string.Join(",\n", priceRows));

            DateOnly D(int days) => DateOnly.FromDateTime(now.AddDays(days).UtcDateTime);
            var past = await f.GameweekAsync("Audit GW past", D(-10), D(-4), now.AddDays(-9));
            var nextDeadline = now.AddDays(3);
            var next = await f.GameweekAsync("Audit GW next", D(2), D(8), nextDeadline);
            var laterDeadline = now.AddDays(10);
            var later = await f.GameweekAsync("Audit GW later", D(9), D(15), laterDeadline);
            var fourthDeadline = now.AddDays(17);
            var fourth = await f.GameweekAsync("Audit GW fourth", D(16), D(22), fourthDeadline);

            var pool = new Pool(clubs,
                rows.ToDictionary(r => r.Key, r => bySm[r.Sm]),
                rows.ToDictionary(r => r.Key, r => r.Sm),
                rows.ToDictionary(r => r.Key, r => r.Price),
                past, next, later, fourth, nextDeadline, laterDeadline, fourthDeadline);
            Pools[f.Db.Name] = pool;
            return pool;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---- Squads ---------------------------------------------------------------------------------

    /// <summary>A squad by keys, so a test can change one slot and see one rule break.</summary>
    public sealed record Spec(string Formation, List<string> Starters, List<string> Bench, string? Captain, string? Vice)
    {
        public Spec Copy() => this with { Starters = [.. Starters], Bench = [.. Bench] };

        public Spec Swap(string from, string to)
        {
            var s = Copy();
            var i = s.Starters.IndexOf(from);
            if (i >= 0) s.Starters[i] = to;
            var j = s.Bench.IndexOf(from);
            if (j >= 0) s.Bench[j] = to;
            return s with { Captain = s.Captain == from ? to : s.Captain, Vice = s.Vice == from ? to : s.Vice };
        }

        public decimal Cost(Pool pool) => Starters.Concat(Bench).Sum(k => pool.Price[k]);

        public object Request(Pool pool, int? gameweekId) => new
        {
            formation = Formation,
            starters = Starters.Select(k => pool[k]).ToArray(),
            bench = Bench.Select(k => pool[k]).ToArray(),
            captainId = Captain is null ? (int?)null : pool[Captain],
            viceCaptainId = Vice is null ? (int?)null : pool[Vice],
            gameweekId,
        };
    }

    /// <summary>The cheapest legal 4-4-2: one player from each of 15 clubs, R67.0m.</summary>
    public static Spec Cheap() => new("4-4-2",
        ["c0-gk1", "c1-def1", "c2-def1", "c3-def1", "c4-def1", "c5-mid1", "c6-mid1", "c7-mid1", "c8-mid1", "c9-att1", "c10-att1"],
        ["c11-gk1", "c12-def1", "c13-mid1", "c14-att1"],
        "c9-att1", "c10-att1");

    /// <summary>A legal 4-4-2 costing exactly R100.0m.</summary>
    public static Spec Exactly100() => new("4-4-2",
        ["c0-gk2", "c1-def5", "c2-def1", "c3-def1", "c4-def1", "c5-mid5", "c6-mid5", "c7-mid5", "c8-mid1", "c9-att3", "c10-att3"],
        ["c11-gk1", "c12-def1", "c13-mid1", "c14-att3"],
        "c9-att3", "c10-att3");

    // ---- Managers -------------------------------------------------------------------------------

    public static HttpClient NewManager(FootballDbFixture f, out Guid sub)
    {
        sub = Guid.NewGuid();
        return f.Api.ClientWithToken(f.Tokens.Create(sub, null));
    }

    public static async Task<HttpClient> ManagerWithTeamAsync(FootballDbFixture f, string name = "Audit XI")
    {
        var client = NewManager(f, out _);
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name }));
        return client;
    }

    public static async Task<HttpClient> ManagerWithSquadAsync(FootballDbFixture f, Pool pool, Spec? spec = null)
    {
        var client = await ManagerWithTeamAsync(f);
        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", (spec ?? Cheap()).Request(pool, pool.NextGameweek)));
        return client;
    }

    /// <summary>Makes the signed-in manager an admin (as an operator would in the database).</summary>
    public static async Task MakeAdminAsync(FootballDbFixture f, HttpClient client)
    {
        var me = await Ok(await client.GetAsync("/api/v1/me"));
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", me.GetProperty("id").GetInt32());
    }

    /// <summary>Runs <paramref name="body"/> with the clock at <paramref name="at"/>, then puts it back.</summary>
    public static async Task AtAsync(FootballDbFixture f, DateTimeOffset at, Func<Task> body)
    {
        var start = f.Clock.Now;
        f.Clock.Now = at;
        try { await body(); }
        finally { f.Clock.Now = start; }
    }

    // ---- HTTP helpers ---------------------------------------------------------------------------

    public static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> Send(Task<HttpResponseMessage> request)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>Asserts an error response: its status, its code, and (optionally) that a named rule is among the violations.</summary>
    public static void AssertError((HttpStatusCode Status, JsonElement Body) r, HttpStatusCode status, string code, string? rule = null)
    {
        Assert.True(r.Status == status, $"expected {(int)status}, got {(int)r.Status}: {r.Body}");
        Assert.True(r.Body.TryGetProperty("code", out var c) && c.GetString() == code, $"expected code {code}: {r.Body}");
        if (rule is not null)
        {
            var rules = r.Body.GetProperty("violations").EnumerateArray().Select(v => v.GetProperty("rule").GetString()).ToList();
            Assert.True(rules.Contains(rule), $"expected rule {rule} among [{string.Join(", ", rules)}]: {r.Body}");
        }
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, object body, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    public static async Task<List<T>> QueryAsync<T>(TestDb db, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var conn = new NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync()) list.Add(map(reader));
        return list;
    }

    public static string Describe(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "(empty)" : e.ToString();

    public static string Utf8(string s) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s));
}
