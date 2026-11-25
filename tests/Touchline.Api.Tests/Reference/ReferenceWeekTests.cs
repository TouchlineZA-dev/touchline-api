using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Data;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Reference;

/// <summary>A database with the whole of GW9 as stored in production (tests/db/reference/gw9.json).</summary>
public sealed class Gw9Fixture : IAsyncLifetime
{
    public string? SkipReason { get; private set; }
    public TestDb Db { get; private set; } = null!;
    public TestApiFactory Api { get; private set; } = null!;
    public JsonElement Data { get; private set; }
    public int SeasonId { get; private set; }

    public async Task InitializeAsync()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        if (server is null) { SkipReason = skip; return; }
        Db = await TestDatabase.CreateAsync(server, "gw9");
        Data = ReferenceData.Gw9();
        SeasonId = await ReferenceData.SeasonAsync(Db);
        await ReferenceData.TeamsAndPlayersAsync(Db, Data);
        await ReferenceData.FixturesAsync(Db, Data, SeasonId);
        Api = new TestApiFactory(Db.ApiConnectionString, new TestTokens(), new TestClock(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)));
    }

    public Task DisposeAsync() { Api?.Dispose(); return Task.CompletedTask; }
}

/// <summary>
/// Sprint 2 reference week (docs/rules/reference-week.md): two squads over the real GW9 data, scored
/// by the production engine, must equal the hand calculation (and tests/db/reference/tools/score_gw9.py).
/// </summary>
public sealed class ReferenceWeekTests(Gw9Fixture f) : IClassFixture<Gw9Fixture>
{
    /// <summary>Per-player GW9 points from the hand calculation, by Sportmonks id.</summary>
    private static readonly IReadOnlyDictionary<long, int> HandCalculated = new Dictionary<long, int>
    {
        // Squad A
        [37423260] = 4, [37583966] = 7, [37773005] = 7, [193219] = 6, [37625553] = 5, [37723175] = 6, [22229131] = 5,
        [1503717] = 4, [38205263] = 4, [37771323] = 0, [37907680] = 7, [37529803] = 4, [38230591] = 4, [1494612] = 3, [38200924] = 5,
        // Squad B
        [37371114] = 4, [37653303] = 6, [289096] = 4, [37762072] = 4, [288954] = 4, [38230282] = 3, [22860319] = 2, [22860322] = 2,
        [37551389] = 5, [37639894] = 4, [22860191] = 4, [289230] = 4, [37604529] = 4, [3877065] = 4, [37566486] = 2,
    };

    private sealed record Squad(string Name, string Formation, string? Chip, int TransferCost, long[] Starters, long[] Bench, long Captain, long Vice, int ExpectedTotal);

    private static IReadOnlyList<Squad> Squads() =>
        JsonDocument.Parse(File.ReadAllText(ReferenceData.Path("reference", "gw9-squads.json"))).RootElement.GetProperty("squads")
            .Deserialize<List<Squad>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private async Task<(Dictionary<long, int> Ids, Dictionary<int, PointsBreakdown> Points)> ScoreGw9()
    {
        using var scope = f.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
        var fixtures = await db.Fixtures.Where(x => x.SeasonId == f.SeasonId).ToListAsync();
        var facts = await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync(fixtures, DateTimeOffset.UtcNow);
        var scoring = new PlayerScoring(new FantasyRules());
        var points = facts.GroupBy(x => x.PlayerId).ToDictionary(g => g.Key, g => scoring.ScoreGameweek(g.Select(x => x.Facts), g.First().Position));
        var ids = await db.Players.ToDictionaryAsync(p => p.SportmonksPlayerId, p => p.Id);
        return (ids, points);
    }

    [SkippableFact]
    public async Task Every_reference_player_scores_what_the_hand_calculation_says()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var (ids, points) = await ScoreGw9();
        foreach (var (smId, expected) in HandCalculated)
        {
            var actual = points.TryGetValue(ids[smId], out var b) ? b.Total : 0;
            Assert.True(expected == actual, $"Sportmonks player {smId}: hand calculation {expected}, engine {actual}");
        }
    }

    [SkippableTheory]
    [InlineData(0, 65)]
    [InlineData(1, 61)]
    public async Task Reference_squad_totals_match_the_hand_calculation(int index, int expected)
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var squad = Squads()[index];
        Assert.Equal(expected, squad.ExpectedTotal);
        var (ids, points) = await ScoreGw9();

        // The squad is legal under the rulebook (positions, club cap, formation, bench).
        var rules = new FantasyRules();
        var players = squad.Starters.Concat(squad.Bench).ToDictionary(sm => ids[sm], sm => FantasyPlayerFor(ids[sm], sm));
        var selection = new SquadSelection(squad.Formation, squad.Starters.Select(sm => ids[sm]).ToList(), squad.Bench.Select(sm => ids[sm]).ToList(), ids[squad.Captain], ids[squad.Vice]);
        var check = new SquadRules(rules).ValidateSquad(selection, players, maxCost: rules.Budget, requireAvailable: false);
        Assert.True(check.IsValid, string.Join("; ", check.Errors));

        var picks = squad.Starters.Select(sm => (sm, starter: true)).Concat(squad.Bench.Select(sm => (sm, starter: false)))
            .Select(p => new TeamPickPoints(ids[p.sm], p.starter, p.sm == squad.Captain, p.sm == squad.Vice,
                points.TryGetValue(ids[p.sm], out var b) ? b : PointsBreakdown.None))
            .ToList();
        var score = new TeamScoring(rules).Score(picks, squad.Chip, squad.TransferCost);
        Assert.Equal(expected, score.Total);

        PickScore Pick(long sm) => score.Picks.Single(p => p.PlayerId == ids[sm]);
        if (index == 0)
        {
            Assert.Equal(1, Pick(squad.Captain).Multiplier);   // captain played 0 minutes
            Assert.Equal(3, Pick(squad.Vice).Multiplier);      // so the vice takes ×3
        }
        else
        {
            Assert.Equal(4, Pick(squad.Captain).Multiplier);   // Triple Captain
            Assert.Equal(2, Pick(squad.Vice).Multiplier);
        }

        // Positions and clubs come from the GW9 snapshot; prices are a flat 6.0 because the snapshot has none.
        FantasyPlayer FantasyPlayerFor(int id, long sm)
        {
            var p = f.Data.GetProperty("players").EnumerateArray().Single(x => x.GetProperty("sportmonks_player_id").GetInt64() == sm);
            var club = f.Data.GetProperty("lineups").EnumerateArray().First(x => x.GetProperty("sportmonks_player_id").ValueKind == JsonValueKind.Number
                && x.GetProperty("sportmonks_player_id").GetInt64() == sm).GetProperty("team_id").GetInt32();
            return new FantasyPlayer(id, p.GetProperty("display_name").GetString()!,
                Touchline.Api.Business.PositionExtensions.FromSportmonks(p.GetProperty("position_id").GetInt16()), club, null, 6.0m);
        }
    }

    [SkippableFact]
    public async Task Rules_endpoint_publishes_version_2026_27_v1_with_every_fpl_difference()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var response = await f.Api.CreateClient().GetAsync("/api/v1/rules");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("2026-27.v1", json.GetProperty("version").GetString());
        Assert.Equal(100.0m, json.GetProperty("squad").GetProperty("budget").GetDecimal());
        Assert.Equal(3, json.GetProperty("captaincy").GetProperty("captainMultiplier").GetInt32());
        Assert.False(json.GetProperty("autoSubs").GetBoolean());
        Assert.False(json.GetProperty("chips").GetProperty("freeHit").GetBoolean());
        Assert.Equal(-2, json.GetProperty("scoring").GetProperty("ownGoal").GetInt32());
        Assert.Equal(
            ["Entry", "Selection", "Deadline", "Captain and bench", "Transfers", "Market prices", "Chips", "Scoring", "Rankings", "Social competition", "Player tools", "Additional modes"],
            json.GetProperty("differencesFromFplClassic").EnumerateArray().Select(d => d.GetProperty("capability").GetString()));
    }
}
