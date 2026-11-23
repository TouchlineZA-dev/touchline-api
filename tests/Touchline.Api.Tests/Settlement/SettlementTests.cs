using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Codes = Touchline.Api.Business.SportmonksCodes;

namespace Touchline.Api.Tests.Settlement;

/// <summary>
/// Sprint 5 (03-Leruo-API Stage 5, handbook Gate 7) against canonical Postgres: the completeness
/// contract, state machine, final and amendment revisions, corrections, interrupted runs and the
/// explanation view. Each test builds its own database so the season is clean.
/// </summary>
public sealed class SettlementTests
{
    private static readonly DateTimeOffset Kickoff = new(2027, 3, 6, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterFullTime = Kickoff.AddHours(3);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>One gameweek with one fixture, two managers with a squad each (15 players from the home side), and an admin.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required TestApiFactory Api { get; init; }
        public required TestClock Clock { get; init; }
        public required TestTokens Tokens { get; init; }
        public required int Gameweek { get; init; }
        public required int Fixture { get; init; }
        public required int Home { get; init; }
        public required int Away { get; init; }
        public required int AdminId { get; init; }
        public required int[] Teams { get; init; }
        public required List<(int Id, long Sm)> Players { get; init; }

        public SettlementService Service(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<SettlementService>();

        public async Task<T> With<T>(Func<SettlementService, Task<T>> action)
        {
            using var scope = Api.Services.CreateScope();
            return await action(Service(scope));
        }

        public Task Event(long smEventId, short type, long? player, long? related, short minute, bool? rescinded = null) => Db.ExecAsync("""
            INSERT INTO football.match_events (sportmonks_event_id, fixture_id, team_id, sportmonks_player_id, sportmonks_related_player_id, type_id, minute, rescinded)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (sportmonks_event_id) DO UPDATE SET sportmonks_player_id = EXCLUDED.sportmonks_player_id,
                sportmonks_related_player_id = EXCLUDED.sportmonks_related_player_id, rescinded = EXCLUDED.rescinded, updated_at = now()
            """, smEventId, Fixture, Home, player, related, type, minute, rescinded);

        public Task<int> Points(int team) => Db.ScalarAsync<int>("SELECT points FROM app.fantasy_gameweek_entries WHERE fantasy_team_id = $1 AND gameweek_id = $2", team, Gameweek);

        public ValueTask DisposeAsync() { Api.Dispose(); return ValueTask.CompletedTask; }
    }

    private static async Task<Scenario> StartAsync(short state = 5, short? home = 1, short? away = 0, bool lineups = true, Dictionary<string, string>? settings = null)
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        Skip.If(server is null, skip);
        var db = await TestDatabase.CreateAsync(server!, "settle");
        var competition = await db.ScalarAsync<int>("INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'PSL') RETURNING id");
        var season = await db.ScalarAsync<int>("INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) VALUES (28123, $1, '2026/2027', true) RETURNING id", competition);
        var homeTeam = await db.ScalarAsync<int>("INSERT INTO football.teams (sportmonks_team_id, name) VALUES (1, 'Home FC') RETURNING id");
        var awayTeam = await db.ScalarAsync<int>("INSERT INTO football.teams (sportmonks_team_id, name) VALUES (2, 'Away FC') RETURNING id");
        var gameweek = await db.ScalarAsync<int>("""
            INSERT INTO app.gameweeks (name, start_date, end_date, season_id, deadline_at, number) VALUES ('GW S', '2027-03-01', '2027-03-07', $1, $2, 1) RETURNING id
            """, season, Kickoff.AddMinutes(-90).UtcDateTime);
        var fixture = await db.ScalarAsync<int>("""
            INSERT INTO football.fixtures (sportmonks_fixture_id, competition_id, season_id, state_id, home_team_id, away_team_id, home_score, away_score, starting_at, length)
            VALUES (777, $1, $2, $3, $4, $5, $6, $7, $8, 90) RETURNING id
            """, competition, season, state, homeTeam, awayTeam, home, away, Kickoff.UtcDateTime);

        var players = new List<(int Id, long Sm)>();
        for (var i = 0; i < 15; i++)
        {
            short position = (short)(i < 2 ? 24 : i < 7 ? 25 : i < 12 ? 26 : 27);
            var id = await db.ScalarAsync<int>("INSERT INTO football.players (sportmonks_player_id, display_name, position_id) VALUES ($1, $2, $3) RETURNING id", 1000L + i, $"P{i}", position);
            players.Add((id, 1000L + i));
            if (lineups)
            {
                await db.ExecAsync("INSERT INTO football.player_lineups (sportmonks_lineup_id, fixture_id, team_id, sportmonks_player_id, type_id) VALUES ($1, $2, $3, $4, 11)",
                    5000L + i, fixture, homeTeam, 1000L + i);
            }
        }

        var admin = await db.ScalarAsync<int>("INSERT INTO app.users (email, username, is_admin) VALUES ('admin@example.com', 'admin_s', true) RETURNING id");
        var teams = new List<int>();
        for (var t = 0; t < 2; t++)
        {
            var user = await db.ScalarAsync<int>("INSERT INTO app.users (email, username) VALUES ($1, $1) RETURNING id", $"m{t}@example.com");
            var team = await db.ScalarAsync<int>("INSERT INTO app.fantasy_teams (user_id, season_id, name) VALUES ($1, $2, $3) RETURNING id", user, season, $"Team {t}");
            teams.Add(team);
            // 4-4-2: bench = P1 (GK), P6 (DEF), P11 (MID), P12 (ATT). Team 0 captains P14, team 1 captains P13.
            foreach (var (p, i) in players.Select((p, i) => (p, i)))
            {
                await db.ExecAsync("""
                    INSERT INTO app.fantasy_squads (fantasy_team_id, player_id, gameweek_id, is_captain, is_vice_captain, is_on_bench, bench_order)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)
                    """, team, p.Id, gameweek, i == (t == 0 ? 14 : 13), i == 10, i is 1 or 6 or 11 or 12,
                    i switch { 1 => (short?)1, 6 => 2, 11 => 3, 12 => 4, _ => null });
            }
            await db.ExecAsync("INSERT INTO app.fantasy_gameweek_entries (fantasy_team_id, gameweek_id, formation) VALUES ($1, $2, '4-4-2')", team, gameweek);
        }

        var clock = new TestClock(AfterFullTime);
        var tokens = new TestTokens();
        return new Scenario
        {
            Db = db, Clock = clock, Tokens = tokens, Gameweek = gameweek, Fixture = fixture, Home = homeTeam, Away = awayTeam, AdminId = admin,
            Teams = [.. teams], Players = players,
            Api = new TestApiFactory(db.ApiConnectionString, tokens, clock, settings),
        };
    }

    [SkippableFact]
    public async Task Without_worker_evidence_the_week_waits_for_review_and_never_finalises_on_final_states_alone()
    {
        await using var s = await StartAsync();
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, s.Players[10].Sm, 20);

        var view = await s.With(x => x.EvaluateAsync(s.Gameweek, s.Clock.Now, default));
        Assert.Equal(SettlementStates.AwaitingReview, view.State);
        Assert.False(view.CanFinalise);
        Assert.Equal(FixtureOutcomes.AwaitingEvidence, view.Fixtures.Single().Outcome);

        var refused = await Assert.ThrowsAsync<ConflictException>(() => s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default)));
        Assert.Equal("settlement_blocked", refused.Code);
        Assert.Equal(0L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_gameweek_entries WHERE scored_at IS NOT NULL"));
    }

    [SkippableFact]
    public async Task A_recorded_resolution_finalises_publishes_a_final_revision_and_a_rerun_is_identical()
    {
        await using var s = await StartAsync();
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, s.Players[10].Sm, 20);
        await s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("manual_resolution", "Checked against the broadcast"), s.AdminId, s.Clock.Now, default));

        var result = await s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default));
        var view = await s.With(x => x.EvaluateAsync(s.Gameweek, s.Clock.Now, default));
        Assert.Equal(SettlementStates.Final, view.State);
        Assert.Equal("final", view.PublishedRevision!.Kind);
        // Team 0 captains the scorer P14 (ATT, full match 2 + goal 3 = 5, ×3 = 15); P10 (MID, vice) assists (2 + 2 = 4, ×2 = 8).
        var team0 = await s.Points(s.Teams[0]);
        var team1 = await s.Points(s.Teams[1]);
        // Away scored 0, so the starting GK and four DEF also keep clean sheets (2 + 2 each): 4 + 16; MID P7–P9 2 each: 6;
        // vice P10 (assist) 4 × 2 = 8; ATT P13 2; captain P14 5 × 3 = 15. Total 51.
        Assert.Equal(51, team0);
        Assert.Equal(51 - 15 + 5 - 2 + 6, team1);     // team 1 captains P13 (2 × 3 = 6) and P14 counts ×1 (5)

        // Repeating settlement gives identical scores and ranks: the correction preview finds nothing to change.
        var preview = JsonSerializer.SerializeToElement(await s.With(x => x.PreviewCorrectionAsync(s.Gameweek, s.Clock.Now, default)), Web);
        Assert.Equal(0, preview.GetProperty("teamsChanged").GetInt32());
        Assert.Empty(preview.GetProperty("overallRankChanges").EnumerateArray());
        await Assert.ThrowsAsync<ConflictException>(() => s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default)));

        var transitions = await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.gameweek_state_transitions WHERE gameweek_id = $1 AND to_state = 'final'", s.Gameweek);
        Assert.Equal(1L, transitions);
        _ = result;
    }

    [SkippableFact]
    public async Task A_corrected_assist_changes_only_the_affected_contributions_through_an_amendment_with_history()
    {
        await using var s = await StartAsync();
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, s.Players[10].Sm, 20);   // assist P10 (MID)
        await s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("manual_resolution", "ok"), s.AdminId, s.Clock.Now, default));
        await s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default));
        var before0 = await s.Points(s.Teams[0]);
        var before1 = await s.Points(s.Teams[1]);

        // Late correction: the assist was P9's (MID), not P10's.
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, s.Players[9].Sm, 20);
        var view = await s.With(x => x.EvaluateAsync(s.Gameweek, s.Clock.Now, default));
        Assert.True(view.AmendmentNeeded);
        Assert.Equal(FixtureOutcomes.LateFacts, view.Fixtures.Single().Outcome);

        var preview = JsonSerializer.SerializeToElement(await s.With(x => x.PreviewCorrectionAsync(s.Gameweek, s.Clock.Now, default)), Web);
        foreach (var team in preview.GetProperty("teams").EnumerateArray())
        {
            Assert.Equal([s.Players[9].Id, s.Players[10].Id], team.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("playerId").GetInt32()).Order());
        }
        var revisionId = preview.GetProperty("currentRevision").GetProperty("id").GetInt32();
        var published = JsonSerializer.SerializeToElement(await s.With(x => x.PublishCorrectionAsync(s.Gameweek,
            new CorrectionRequest("Sportmonks correction 1 Nov", "Assist re-attributed from P10 to P9", revisionId), s.AdminId, s.Clock.Now, default)), Web);
        Assert.Equal("amendment", published.GetProperty("revision").GetProperty("kind").GetString());
        Assert.Equal(revisionId, published.GetProperty("revision").GetProperty("previousRevisionId").GetInt32());

        // P10 was team 0's vice (×2): 4→2 base, contribution 8→4; P9 gains 2. Team 0: −4 + 2 = −2. Team 1: same players, vice P10 too: −2.
        Assert.Equal(before0 - 2, await s.Points(s.Teams[0]));
        Assert.Equal(before1 - 2, await s.Points(s.Teams[1]));
        var history = await s.With(x => x.HistoryAsync(s.Gameweek, default));
        Assert.Equal(["final", "amendment"], history.Where(h => h.Status == "published").Select(h => h.Kind));
    }

    [SkippableFact]
    public async Task A_rescinded_goal_is_removed_by_an_amendment_and_the_explain_view_says_why_the_score_dropped()
    {
        await using var s = await StartAsync();
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, null, 20);
        await s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("manual_resolution", "ok"), s.AdminId, s.Clock.Now, default));
        await s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default));
        var before = await s.Points(s.Teams[0]);

        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, null, 20, rescinded: true);
        await s.Db.ExecAsync("UPDATE football.fixtures SET home_score = 0 WHERE id = $1", s.Fixture);
        await s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("manual_resolution", "Goal ruled out"), s.AdminId, s.Clock.Now, default));
        var current = (await s.With(x => x.EvaluateAsync(s.Gameweek, s.Clock.Now, default))).PublishedRevision!.Id;
        await s.With(x => x.PublishCorrectionAsync(s.Gameweek, new CorrectionRequest("VAR", "Goal rescinded", current), s.AdminId, s.Clock.Now, default));
        Assert.Equal(before - 9, await s.Points(s.Teams[0]));   // captain's goal: 3 × 3

        using var scope = s.Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Touchline.Api.Data.TouchlineDbContext>();
        var team = db.FantasyTeams.Single(t => t.Id == s.Teams[0]);
        var calendar = await scope.ServiceProvider.GetRequiredService<GameweekService>().GetCalendarAsync(team.SeasonId);
        var explain = JsonSerializer.SerializeToElement(await s.Service(scope).ExplainAsync(team, calendar.ById(s.Gameweek)!, calendar, s.Clock.Now, default),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(before, explain.GetProperty("previousTotal").GetInt32());
        var change = explain.GetProperty("changesSincePreviousRevision").EnumerateArray().Single();
        Assert.Equal(s.Players[14].Id, change.GetProperty("playerId").GetInt32());
        Assert.Contains("goals 1 → 0", change.GetProperty("because").GetString());
    }

    [SkippableFact]
    public async Task A_quiet_0_0_is_told_apart_from_missing_data()
    {
        await using var quiet = await StartAsync(home: 0, away: 0);
        var view = await quiet.With(x => x.EvaluateAsync(quiet.Gameweek, quiet.Clock.Now, default));
        Assert.Equal(FixtureOutcomes.AwaitingEvidence, view.Fixtures.Single().Outcome);   // could be quiet or delayed
        await quiet.With(x => x.ResolveFixtureAsync(quiet.Fixture, new ResolveFixtureRequest("verified_zero", "Watched it: no goals, no cards"), quiet.AdminId, quiet.Clock.Now, default));
        Assert.True((await quiet.With(x => x.EvaluateAsync(quiet.Gameweek, quiet.Clock.Now, default))).CanFinalise);

        await using var missing = await StartAsync(home: 0, away: 0, lineups: false);
        var missingView = await missing.With(x => x.EvaluateAsync(missing.Gameweek, missing.Clock.Now, default));
        Assert.Equal(FixtureOutcomes.MissingData, missingView.Fixtures.Single().Outcome);
        Assert.False(missingView.CanFinalise);
    }

    [SkippableFact]
    public async Task An_unattributed_goal_holds_the_fixture_for_48_hours_even_with_worker_evidence()
    {
        await using var s = await StartAsync();
        await s.Event(1, Codes.EventTypes.Goal, null, null, 20);
        await s.Db.ExecAsync("INSERT INTO sync.gameweek_sync_status (gameweek_id, fixtures_complete, events_complete, stats_complete, marked_complete) VALUES ($1, true, true, true, true)", s.Gameweek);

        var held = await s.With(x => x.EvaluateAsync(s.Gameweek, s.Clock.Now, default));
        Assert.Equal(FixtureOutcomes.UnattributedHold, held.Fixtures.Single().Outcome);
        await Assert.ThrowsAsync<ConflictException>(() =>
            s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("unattributed_resolved", "too early"), s.AdminId, s.Clock.Now, default)));

        s.Clock.Now = AfterFullTime.AddHours(49);
        var after = await s.With(x => x.EvaluateAsync(s.Gameweek, s.Clock.Now, default));
        Assert.Equal(FixtureOutcomes.Complete, after.Fixtures.Single().Outcome);
        Assert.True(after.CanFinalise);
    }

    [SkippableFact]
    public async Task An_interrupted_run_resumes_without_double_counting()
    {
        await using var s = await StartAsync(settings: new Dictionary<string, string> { ["Settlement:BatchSize"] = "1" });
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, null, 20);
        await s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("manual_resolution", "ok"), s.AdminId, s.Clock.Now, default));

        await Assert.ThrowsAsync<OperationCanceledException>(() => s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default, batch => batch == 1)));
        Assert.Equal(1L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.score_revision_teams"));
        Assert.Equal(0L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_gameweek_entries WHERE scored_at IS NOT NULL"));
        Assert.Equal("building", await s.Db.ScalarAsync<string>("SELECT status FROM app.score_revisions"));

        await s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default));
        Assert.Equal(1L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.score_revisions"));        // resumed, not restarted
        Assert.Equal(2L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.score_revision_teams"));
        Assert.Equal(30L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.points_results"));        // 15 per team, once
        Assert.Equal(2L, await s.Db.ScalarAsync<long>("SELECT count(*) FROM app.fantasy_gameweek_entries WHERE scored_at IS NOT NULL"));
    }

    [SkippableFact]
    public async Task Non_admins_cannot_score_settle_correct_or_price()
    {
        await using var s = await StartAsync();
        var fan = s.Api.ClientWithToken(s.Tokens.Create(Guid.NewGuid(), null));
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Post, $"/api/v1/admin/gameweeks/{s.Gameweek}/score"),
                     (HttpMethod.Get, $"/api/v1/admin/gameweeks/{s.Gameweek}/settlement"),
                     (HttpMethod.Post, $"/api/v1/admin/gameweeks/{s.Gameweek}/corrections/preview"),
                     (HttpMethod.Post, "/api/v1/admin/prices/recalculate?windowId=1"),
                 })
        {
            var response = await fan.SendAsync(new HttpRequestMessage(method, url) { Content = method == HttpMethod.Post ? JsonContent.Create(new { }) : null });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        var resolve = await fan.PostAsJsonAsync($"/api/v1/admin/fixtures/{s.Fixture}/resolution", new { outcome = "verified_zero", reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resolve.StatusCode);
    }

    [SkippableFact]
    public async Task Standings_come_from_the_published_revision_paged_with_my_row_and_explain_works_over_http()
    {
        await using var s = await StartAsync();
        await s.Event(1, Codes.EventTypes.Goal, s.Players[14].Sm, s.Players[10].Sm, 20);
        await s.With(x => x.ResolveFixtureAsync(s.Fixture, new ResolveFixtureRequest("manual_resolution", "ok"), s.AdminId, s.Clock.Now, default));
        var final = await s.With(x => x.FinaliseAsync(s.Gameweek, s.AdminId, s.Clock.Now, default));
        var revisionId = JsonSerializer.SerializeToElement(final, Web).GetProperty("revision").GetProperty("id").GetInt32();

        var sub = Guid.NewGuid();
        await s.Db.ExecAsync("UPDATE app.users SET auth_user_id = $1 WHERE id = (SELECT user_id FROM app.fantasy_teams WHERE id = $2)", sub, s.Teams[1]);
        var client = s.Api.ClientWithToken(s.Tokens.Create(sub, null));

        var page = await FantasyWorldOk(await client.GetAsync("/api/v1/fantasy/leagues/overall/standings?pageSize=1"));
        Assert.Equal($"r{revisionId}", page.GetProperty("revision").GetString());
        Assert.Equal(1, page.GetProperty("rows").GetArrayLength());
        Assert.Equal(2, page.GetProperty("totalRows").GetInt32());
        Assert.Equal(s.Teams[0], page.GetProperty("rows")[0].GetProperty("fantasyTeamId").GetInt32());   // 51 beats 45
        Assert.Equal(2, page.GetProperty("me").GetProperty("rank").GetInt32());

        var explain = await FantasyWorldOk(await client.GetAsync("/api/v1/fantasy/team/points/explain?gameweek=1"));
        Assert.False(explain.GetProperty("provisional").GetBoolean());
        Assert.Equal(45, explain.GetProperty("total").GetInt32());
        var captain = explain.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("isCaptain").GetBoolean());
        Assert.Contains("Captain played", captain.GetProperty("reason").GetString());

        var points = await FantasyWorldOk(await client.GetAsync("/api/v1/fantasy/team/points?gameweek=1"));
        Assert.Equal("final", points.GetProperty("revision").GetProperty("kind").GetString());
    }

    private static async Task<JsonElement> FantasyWorldOk(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public void A_defender_off_at_60_before_a_late_concession_follows_the_whole_match_clean_sheet_rule()
    {
        // Rulebook §9: a clean sheet needs 60+ minutes and the team conceding 0 in the match. The defender left at 60
        // with nothing conceded while he was on; the team conceded at 80. Pitch-time variant (FPL) would award it; ours doesn't.
        var scoring = new PlayerScoring(new FantasyRules());
        var facts = new PlayerFixtureFacts { FixtureId = 1, Started = true, WentOffMinute = 60, MatchEndMinute = 90, FixtureFinished = true, GoalsConceded = 1 };
        var points = scoring.ScoreFixture(facts, Touchline.Api.Business.Position.DEF);
        Assert.Equal(60, points.Minutes);
        Assert.Equal(0, points.CleanSheets);
        Assert.Equal(1, points.Total);   // appearance only
        var sixtyNil = scoring.ScoreFixture(facts with { GoalsConceded = 0 }, Touchline.Api.Business.Position.DEF);
        Assert.Equal(1, sixtyNil.CleanSheets);   // 60 minutes is enough when the team keeps the clean sheet
    }
}
