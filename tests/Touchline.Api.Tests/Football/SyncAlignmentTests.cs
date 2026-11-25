using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Business;
using Touchline.Api.Data;
using Touchline.Api.Services;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Football;

/// <summary>
/// Sprint 8: alignment with the sync owner's V040/V041 (withdrawn events and lineups, fixture freshness,
/// live fixture health) and his live-state list. Each behaviour is tested with the objects present and,
/// where it matters, on a database from before they exist.
/// </summary>
public sealed class SyncAlignmentTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private async Task<JsonElement> Detail(int fixtureId)
    {
        var response = await f.Api.CreateClient().GetAsync($"/api/v1/fixtures/{fixtureId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<int> GoalsFor(int fixtureId, int playerId)
    {
        using var scope = f.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
        var fixture = await db.Fixtures.SingleAsync(x => x.Id == fixtureId);
        var facts = await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync([fixture], f.Clock.Now);
        return facts.Where(x => x.PlayerId == playerId).Sum(x => x.Facts.Goals);
    }

    [SkippableFact]
    public async Task Withdrawn_events_and_lineup_rows_are_excluded_from_detail_scoring_and_revision()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddHours(-3), Codes.MatchStates.FullTime, 1, 0);
        var scorer = await f.PlayerAsync("Withdrawn Scorer");
        var dropped = await f.PlayerAsync("Dropped Starter");
        await f.LineupAsync(fixture, f.Home, scorer.SportmonksId, 11);
        await f.LineupAsync(fixture, f.Home, dropped.SportmonksId, 11);
        var eventId = FootballDbFixture.NextId();
        await f.EventAsync(fixture, eventId, Codes.EventTypes.Goal, f.Home, scorer.SportmonksId, null, 15);
        var before = await Detail(fixture);
        Assert.Equal(1, await GoalsFor(fixture, scorer.Id));

        // The worker withdraws the goal (VAR) and the dropped player's lineup row (complete XI without him).
        await f.Db.ExecAsync("UPDATE football.match_events SET withdrawn_at = now() WHERE sportmonks_event_id = $1", (long)eventId);
        await f.Db.ExecAsync("UPDATE football.player_lineups SET withdrawn_at = now() WHERE fixture_id = $1 AND sportmonks_player_id = $2", fixture, dropped.SportmonksId);

        var after = await Detail(fixture);
        Assert.Equal(0, after.GetProperty("events").GetProperty("total").GetInt32());
        Assert.NotEqual(before.GetProperty("revision").GetString(), after.GetProperty("revision").GetString());
        Assert.DoesNotContain(after.GetProperty("lineups").GetProperty("home").GetProperty("startingXi").EnumerateArray(),
            l => l.GetProperty("player").GetProperty("playerId").ValueKind == JsonValueKind.Number && l.GetProperty("player").GetProperty("playerId").GetInt32() == dropped.Id);
        Assert.Equal(0, await GoalsFor(fixture, scorer.Id));
    }

    [SkippableFact]
    public async Task Before_v040_every_event_counts_and_revision_and_freshness_use_the_api_fallback()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        Skip.If(server is null, skip);
        var db = await TestDatabase.CreateAsync(server!, "pre_v040", upToVersion: 39);
        var competition = await db.ScalarAsync<int>("INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'PSL') RETURNING id");
        var season = await db.ScalarAsync<int>("INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) VALUES (28123, $1, '2026/2027', true) RETURNING id", competition);
        var home = await db.ScalarAsync<int>("INSERT INTO football.teams (sportmonks_team_id, name) VALUES (1, 'Home') RETURNING id");
        var away = await db.ScalarAsync<int>("INSERT INTO football.teams (sportmonks_team_id, name) VALUES (2, 'Away') RETURNING id");
        var player = await db.ScalarAsync<int>("INSERT INTO football.players (sportmonks_player_id, display_name, position_id) VALUES (77, 'Old Schema', 27) RETURNING id");
        var fixture = await db.ScalarAsync<int>("""
            INSERT INTO football.fixtures (sportmonks_fixture_id, competition_id, season_id, state_id, home_team_id, away_team_id, home_score, away_score, starting_at, length)
            VALUES (9, $1, $2, 5, $3, $4, 1, 0, now() - interval '3 hours', 90) RETURNING id
            """, competition, season, home, away);
        await db.ExecAsync("INSERT INTO football.player_lineups (sportmonks_lineup_id, fixture_id, team_id, sportmonks_player_id, type_id) VALUES (1, $1, $2, 77, 11)", fixture, home);
        await db.ExecAsync("INSERT INTO football.match_events (sportmonks_event_id, fixture_id, team_id, sportmonks_player_id, type_id, minute) VALUES (1, $1, $2, 77, 14, 10)", fixture, home);
        Assert.False(await db.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'football' AND table_name = 'match_events' AND column_name = 'withdrawn_at')"));

        using var api = new TestApiFactory(db.ApiConnectionString, new TestTokens());
        var response = await api.CreateClient().GetAsync($"/api/v1/fixtures/{fixture}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, detail.GetProperty("events").GetProperty("total").GetInt32());
        Assert.Equal("api_content_hash", detail.GetProperty("revisionSource").GetString());
        Assert.Equal("api", detail.GetProperty("freshness").GetProperty("source").GetString());

        using var scope = api.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
        var facts = await scope.ServiceProvider.GetRequiredService<MatchFactsService>().LoadAsync([await ctx.Fixtures.SingleAsync(x => x.Id == fixture)], DateTimeOffset.UtcNow);
        Assert.Equal(1, facts.Single(x => x.PlayerId == player).Facts.Goals);
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/api/v1/health/freshness")).StatusCode);
    }

    private Task FreshnessRow(int fixtureId, int revision, string observedAgo, string changedAgo) => f.Db.ExecAsync($"""
        INSERT INTO sync.fixture_freshness (fixture_id, sportmonks_fixture_id, revision, content_hash, last_observed_at, last_committed_at, last_changed_at)
        SELECT id, sportmonks_fixture_id, $2, 'h', now() - interval '{observedAgo}', now() - interval '{observedAgo}', now() - interval '{changedAgo}'
        FROM football.fixtures WHERE id = $1
        ON CONFLICT (fixture_id) DO UPDATE SET revision = EXCLUDED.revision, last_observed_at = EXCLUDED.last_observed_at,
            last_committed_at = EXCLUDED.last_committed_at, last_changed_at = EXCLUDED.last_changed_at
        """, fixtureId, revision);

    [SkippableFact]
    public async Task Live_freshness_and_revision_come_from_the_worker_and_stale_means_a_stalled_poller()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var fixture = await f.FixtureAsync(f.Clock.Now.AddMinutes(-50), Codes.MatchStates.SecondHalf, 0, 0);

        // Observed 30 s ago, nothing changed for 20 minutes: a quiet spell, not stale.
        await FreshnessRow(fixture, 7, "30 seconds", "20 minutes");
        var quiet = await Detail(fixture);
        Assert.Equal("s7", quiet.GetProperty("revision").GetString());
        Assert.Equal("sync.fixture_freshness", quiet.GetProperty("revisionSource").GetString());
        Assert.Equal("healthy_quiet", quiet.GetProperty("freshness").GetProperty("pollHealth").GetString());
        Assert.False(quiet.GetProperty("freshness").GetProperty("stale").GetBoolean());

        // Not observed for 10 minutes: the poller stalled.
        await FreshnessRow(fixture, 7, "10 minutes", "20 minutes");
        var stalled = await Detail(fixture);
        Assert.Equal("stalled", stalled.GetProperty("freshness").GetProperty("pollHealth").GetString());
        Assert.True(stalled.GetProperty("freshness").GetProperty("stale").GetBoolean());

        // The operator sees it in diagnostics and the public freshness check counts it.
        var adminClient = f.Api.ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));
        var me = JsonDocument.Parse(await adminClient.GetStringAsync("/api/v1/me")).RootElement;
        await f.Db.ExecAsync("UPDATE app.users SET is_admin = true WHERE id = $1", me.GetProperty("id").GetInt32());
        var diagnostics = JsonDocument.Parse(await adminClient.GetStringAsync("/api/v1/admin/diagnostics")).RootElement;
        Assert.NotEmpty(diagnostics.GetProperty("alerts").EnumerateArray());
        Assert.Contains(diagnostics.GetProperty("stalledLiveFixtures").GetProperty("fixtures").EnumerateArray(), x => x.GetProperty("fixtureId").GetInt32() == fixture);
        Assert.Equal("sync.live_fixture_health", diagnostics.GetProperty("stalledLiveFixtures").GetProperty("source").GetString());
        var publicFreshness = JsonDocument.Parse(await f.Api.CreateClient().GetStringAsync("/api/v1/health/freshness")).RootElement;
        Assert.True(publicFreshness.GetProperty("stalledLiveFixtures").GetInt32() >= 1);
        Assert.False(publicFreshness.GetProperty("fresh").GetBoolean());

        await f.Db.ExecAsync("UPDATE football.fixtures SET state_id = 5 WHERE id = $1", fixture);   // over: leaves the live view
    }

    [Fact]
    public void Should_be_live_matches_the_sync_owners_list_in_v040()
    {
        var sql = File.ReadAllText(Path.Combine(TestDatabase.RepoRoot(), "tests", "db", "flyway", "V040__live_freshness_and_quota.sql"));
        var list = Regex.Match(sql, @"state_id IN \(([\d, ]+)\)").Groups[1].Value.Split(',').Select(x => short.Parse(x.Trim())).Order().ToList();
        Assert.Equal(new short[] { 2, 3, 4, 6, 9, 11, 16, 18, 19, 21, 22, 23, 25, 26 }, list.ToArray());
        Assert.Equal(list, MatchStateMatrix.ShouldBeLiveStates.Order());

        // Every state our matrix treats as Live should be live for the worker too; the worker's list adds
        // suspended (11), delayed (16), awaiting updates (19) and pending (26), which we watch but don't score as live.
        var ourLive = MatchStateMatrix.All.Where(s => s.Status == MatchStatus.Live).Select(s => s.StateId!.Value).ToHashSet();
        Assert.Subset(MatchStateMatrix.ShouldBeLiveStates.ToHashSet(), ourLive);
        Assert.Equal(new short[] { 11, 16, 19, 26 }, MatchStateMatrix.ShouldBeLiveStates.Except(ourLive).Order().ToArray());
    }
}
