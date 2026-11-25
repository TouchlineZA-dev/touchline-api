using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;

namespace Touchline.Api.Tests.SafeWrites;

/// <summary>
/// A playable fantasy world on top of FootballDbFixture: 6 clubs with 6 priced, pickable players each,
/// a locked past gameweek and an open next gameweek. Managers sign in with fresh Supabase users.
/// </summary>
public sealed class FantasyWorld
{
    public sealed record Pool(Dictionary<string, int> Players, int PastGameweek, int NextGameweek, List<int> Clubs, Dictionary<string, long> SportmonksIds);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Pool> Pools = [];

    /// <summary>Seeds the world once per database.</summary>
    public static async Task<Pool> EnsureAsync(FootballDbFixture f)
    {
        await Gate.WaitAsync();
        try
        {
            if (Pools.TryGetValue(f.Db.Name, out var existing)) return existing;
            var players = new Dictionary<string, int>();
            var smIds = new Dictionary<string, long>();
            var clubs = new List<int>();
            for (var c = 0; c < 6; c++) clubs.Add(await f.TeamAsync($"Club {c}", $"C{c}"));

            // One finished fixture per pair of clubs, with every player in the lineup: that makes them pickable.
            var fixtures = new List<int>();
            for (var c = 0; c < 6; c += 2)
            {
                fixtures.Add(await f.FixtureAsync(f.Clock.Now.AddDays(-30 + c), 5, 1, 0, clubs[c], clubs[c + 1]));
            }
            (string Code, short Position, decimal Price)[] roster = [("gk", 24, 4.5m), ("def1", 25, 5.0m), ("def2", 25, 5.0m), ("mid1", 26, 6.0m), ("mid2", 26, 6.0m), ("att", 27, 7.0m)];
            for (var c = 0; c < 6; c++)
            {
                foreach (var (code, position, price) in roster)
                {
                    var p = await f.PlayerAsync($"C{c} {code}", position);
                    players[$"c{c}-{code}"] = p.Id;
                    smIds[$"c{c}-{code}"] = p.SportmonksId;
                    await f.LineupAsync(fixtures[c / 2], clubs[c], p.SportmonksId, 11);
                    await f.Db.ExecAsync("INSERT INTO app.player_prices (player_id, season_id, price) VALUES ($1, $2, $3)", p.Id, f.SeasonId, price);
                }
            }

            var past = await f.GameweekAsync("World GW past", DateOnly.FromDateTime(f.Clock.Now.AddDays(-10).UtcDateTime), DateOnly.FromDateTime(f.Clock.Now.AddDays(-4).UtcDateTime), f.Clock.Now.AddDays(-9));
            var next = await f.GameweekAsync("World GW next", DateOnly.FromDateTime(f.Clock.Now.AddDays(2).UtcDateTime), DateOnly.FromDateTime(f.Clock.Now.AddDays(8).UtcDateTime), f.Clock.Now.AddDays(3));
            var pool = new Pool(players, past, next, clubs, smIds);
            Pools[f.Db.Name] = pool;
            return pool;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A legal 4-4-2 with at most 3 per club (see the comment for the club spread).</summary>
    public static object Squad(Pool pool, int gameweekId)
    {
        int P(string key) => pool.Players[key];
        // GK c0 (bench c1); DEF c2 c3 c4 c5 (+c2 bench); MID c3 c4 c5 c0 (+c1 bench); ATT c0 c1 (+c3 bench).
        int[] starters = [P("c0-gk"), P("c2-def1"), P("c3-def1"), P("c4-def1"), P("c5-def1"), P("c3-mid1"), P("c4-mid1"), P("c5-mid1"), P("c0-mid1"), P("c0-att"), P("c1-att")];
        int[] bench = [P("c1-gk"), P("c2-def2"), P("c1-mid1"), P("c3-att")];
        return new { formation = "4-4-2", starters, bench, captainId = P("c0-att"), viceCaptainId = P("c1-att"), gameweekId };
    }

    public static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> Send(Task<HttpResponseMessage> request)
    {
        var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>A signed-in manager with a team and a saved squad for the next gameweek.</summary>
    public static async Task<HttpClient> ManagerWithSquadAsync(FootballDbFixture f, Pool pool)
    {
        var client = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Safe Writes XI" }));
        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        return client;
    }
}
