using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Contract;

/// <summary>What a container host (Railway) relies on: liveness, the health summary, the listen port and a buildable image.</summary>
public sealed class DeploymentTests
{
    private const string NoDatabase = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Get(TestApiFactory api, string path)
    {
        var response = await api.CreateClient().GetAsync(path);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [Fact]
    public async Task Live_is_200_without_a_database_and_health_is_503_unavailable_only_then()
    {
        using var api = new TestApiFactory(NoDatabase, new TestTokens());
        var live = await Get(api, "/api/v1/health/live");
        Assert.Equal(HttpStatusCode.OK, live.Status);
        Assert.Equal("live", live.Body.GetProperty("status").GetString());

        var health = await Get(api, "/api/v1/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.Status);
        Assert.Equal("unavailable", health.Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get(api, "/api/v1/health/ready")).Status);
    }

    [SkippableFact]
    public async Task Health_is_200_degraded_for_stale_data_and_200_ok_when_fresh()
    {
        var (server, skip) = await TestDatabase.ServerAsync();
        Skip.If(server is null, skip);
        var db = await TestDatabase.CreateAsync(server!, "health");
        using var api = new TestApiFactory(db.ApiConnectionString, new TestTokens());

        // No football data at all: the database answers, the data is stale.
        var stale = await Get(api, "/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, stale.Status);
        Assert.Equal("degraded", stale.Body.GetProperty("status").GetString());
        Assert.Equal(["status", "checkedAtUtc"], stale.Body.EnumerateObject().Select(p => p.Name));

        // A fresh football commit (a scheduled fixture written now).
        var competition = await db.ScalarAsync<int>("INSERT INTO football.competitions (sportmonks_competition_id, name) VALUES (806, 'PSL') RETURNING id");
        var season = await db.ScalarAsync<int>("INSERT INTO football.seasons (sportmonks_season_id, competition_id, name, is_current) VALUES (28123, $1, '2026/2027', true) RETURNING id", competition);
        await db.ExecAsync("""
            INSERT INTO football.fixtures (sportmonks_fixture_id, competition_id, season_id, state_id, starting_at)
            VALUES (1, $1, $2, 1, now() + interval '2 days')
            """, competition, season);
        using var fresh = new TestApiFactory(db.ApiConnectionString, new TestTokens());
        var ok = await Get(fresh, "/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("ok", ok.Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Get(fresh, "/api/v1/health/live")).Status);
    }

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("8080", 8080)]
    [InlineData("5000", 5000)]
    [InlineData("not-a-port", 8080)]
    [InlineData("0", 8080)]
    [InlineData("70000", 8080)]
    public void The_app_listens_on_PORT_when_it_is_a_valid_port_otherwise_8080(string? port, int expected) =>
        Assert.Equal(expected, ListenPort.From(port));

    [Fact]
    public void Every_file_the_Dockerfile_copies_exists_and_is_not_excluded_by_dockerignore()
    {
        var root = TestDatabase.RepoRoot();
        var dockerfile = File.ReadAllLines(Path.Combine(root, "Dockerfile"));
        var ignore = File.ReadAllLines(Path.Combine(root, ".dockerignore"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

        var sources = dockerfile.Select(l => l.Trim())
            .Where(l => l.StartsWith("COPY ", StringComparison.Ordinal) && !l.Contains("--from=", StringComparison.Ordinal))
            .SelectMany(l => l[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
            .ToList();
        Assert.NotEmpty(sources);
        Assert.Contains("Directory.Build.props", sources);
        foreach (var source in sources)
        {
            var path = Path.Combine(root, source.TrimEnd('/'));
            Assert.True(File.Exists(path) || Directory.Exists(path), $"Dockerfile copies {source}, which isn't in the repo");
            foreach (var pattern in ignore)
            {
                Assert.False(Excludes(pattern, source.TrimEnd('/')), $".dockerignore pattern '{pattern}' excludes {source}, which the Dockerfile needs");
            }
        }

        Assert.Contains(dockerfile, l => l.Contains("ENTRYPOINT", StringComparison.Ordinal) && l.Contains("Touchline.Api.dll", StringComparison.Ordinal));
        var railway = File.ReadAllText(Path.Combine(root, "railway.toml"));
        Assert.Contains("healthcheckPath = \"/api/v1/health/live\"", railway);
    }

    /// <summary>A simple .dockerignore match: the pattern names the path or one of its parent folders (with * and ** wildcards).</summary>
    private static bool Excludes(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern.TrimStart('/').TrimEnd('/')).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "(/.*)?$";
        return Regex.IsMatch(path, regex);
    }
}
