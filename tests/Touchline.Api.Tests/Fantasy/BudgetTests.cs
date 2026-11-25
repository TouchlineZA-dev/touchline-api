using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Touchline.Api.Fantasy;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;
using Touchline.Api.Tests.Football;
using static Touchline.Api.Tests.SafeWrites.FantasyWorld;

namespace Touchline.Api.Tests.Fantasy;

/// <summary>
/// The agreed budget is R100.0m, from one setting (FantasyRules.Budget, env override Fantasy__Budget),
/// and every path reads it: team creation, squad validation, GET /rules and the snapshot bank.
/// Testers once saw R300m because a deploy carried a different value; these tests pin the effective one.
/// </summary>
public sealed class BudgetTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    // The world squad (FantasyWorld.Squad) costs R85.0m: GKs 4.5 + 4.5, DEF 5 × 5.0, MID 5 × 6.0, ATT 3 × 7.0.
    private static readonly string[] Attackers = ["c0-att", "c1-att", "c3-att"];

    /// <summary>Prices the squad's three attackers and its starting goalkeeper (the cost moves by the difference).</summary>
    private async Task PriceAsync(Pool pool, decimal attacker, decimal goalkeeper)
    {
        foreach (var key in Attackers)
            await f.Db.ExecAsync("UPDATE app.player_prices SET price = $1 WHERE player_id = $2 AND season_id = $3", attacker, pool.Players[key], f.SeasonId);
        await f.Db.ExecAsync("UPDATE app.player_prices SET price = $1 WHERE player_id = $2 AND season_id = $3", goalkeeper, pool.Players["c0-gk"], f.SeasonId);
    }

    private async Task<Pool> WorldAsync()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        return await EnsureAsync(f);
    }

    private HttpClient NewManager(TestApiFactory? api = null) => (api ?? f.Api).ClientWithToken(f.Tokens.Create(Guid.NewGuid(), null));

    [SkippableFact]
    public void The_effective_budget_is_R100m_with_no_override()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        // What the running API actually binds (appsettings + environment), not just the class default.
        Assert.Equal(100.0m, f.Api.Services.GetRequiredService<IOptions<FantasyRules>>().Value.Budget);
        Assert.Equal(100.0m, new FantasyRules().Budget);
    }

    [SkippableFact]
    public async Task A_new_team_starts_with_exactly_R100m()
    {
        await WorldAsync();
        var team = await Ok(await NewManager().PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Budget XI" }));
        Assert.Equal(100.0m, team.GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public async Task Rules_publish_a_R100m_budget()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var rules = await Ok(await f.Api.CreateClient().GetAsync("/api/v1/rules"));
        Assert.Equal(100.0m, rules.GetProperty("squad").GetProperty("budget").GetDecimal());
    }

    [SkippableFact]
    public async Task A_squad_costing_exactly_R100m_is_allowed_and_leaves_a_zero_bank()
    {
        var pool = await WorldAsync();
        await PriceAsync(pool, attacker: 12.0m, goalkeeper: 4.5m);   // 85.0 + 3 × 5.0 = 100.0
        var client = NewManager();
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Exact XI" }));

        var saved = await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        Assert.Equal(0.0m, saved.GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public async Task A_squad_costing_R100_1m_is_rejected_and_the_bank_is_untouched()
    {
        var pool = await WorldAsync();
        await PriceAsync(pool, attacker: 12.0m, goalkeeper: 4.6m);   // 100.0 + 0.1 = 100.1
        var client = NewManager();
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Over XI" }));

        var (status, body) = await Send(client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("The squad costs R100.1m, which is over the R100.0m budget.",
            body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        var team = await Ok(await client.GetAsync("/api/v1/fantasy/team"));
        Assert.Equal(100.0m, team.GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public async Task The_snapshot_bank_is_R100m_minus_the_squad_cost()
    {
        var pool = await WorldAsync();
        await PriceAsync(pool, attacker: 7.0m, goalkeeper: 4.5m);    // the world's own prices: R85.0m
        var client = NewManager();
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Snapshot XI" }));
        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));

        var team = (await Ok(await client.GetAsync("/api/v1/fantasy/team/snapshot"))).GetProperty("team");
        var squadCost = team.GetProperty("squadValue").GetDecimal();
        Assert.Equal(85.0m, squadCost);
        Assert.Equal(100.0m - squadCost, team.GetProperty("budgetRemaining").GetDecimal());
    }

    /// <summary>
    /// Proves there is no second, hard-coded budget: one setting change (what Fantasy__Budget does on a host)
    /// moves team creation, validation, /rules and the snapshot together.
    /// </summary>
    [SkippableFact]
    public async Task One_setting_drives_creation_validation_rules_and_the_snapshot()
    {
        var pool = await WorldAsync();
        await PriceAsync(pool, attacker: 7.0m, goalkeeper: 4.5m);    // R85.0m
        using var api = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["Fantasy:Budget"] = "84.5" });

        Assert.Equal(84.5m, (await Ok(await api.CreateClient().GetAsync("/api/v1/rules"))).GetProperty("squad").GetProperty("budget").GetDecimal());
        var over = NewManager(api);
        Assert.Equal(84.5m, (await Ok(await over.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Tight XI" }))).GetProperty("budgetRemaining").GetDecimal());
        var (status, _) = await Send(over.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        Assert.Equal(HttpStatusCode.BadRequest, status);

        using var roomy = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["Fantasy:Budget"] = "90.0" });
        var client = NewManager(roomy);
        await Ok(await client.PostAsJsonAsync("/api/v1/fantasy/team", new { name = "Roomy XI" }));
        await Ok(await client.PutAsJsonAsync("/api/v1/fantasy/team/squad", Squad(pool, pool.NextGameweek)));
        var team = (await Ok(await client.GetAsync("/api/v1/fantasy/team/snapshot"))).GetProperty("team");
        Assert.Equal(5.0m, team.GetProperty("budgetRemaining").GetDecimal());
    }

    [SkippableFact]
    public void The_effective_budget_is_logged_once_at_startup()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var logs = new CapturingLoggerProvider();
        using var api = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock)
            .WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        _ = api.Services;   // builds the host, which runs Program.cs up to app.Run

        Assert.Single(logs.Messages, m => m == "Fantasy budget: 100.0");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
