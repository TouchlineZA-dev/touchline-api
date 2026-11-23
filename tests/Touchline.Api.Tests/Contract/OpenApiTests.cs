using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Contract;

/// <summary>
/// docs/api/openapi-v1.json is exported from the running app. This fails when the checked-in file is
/// out of date (CI runs it), so the published contract always matches the code. To refresh it:
/// TOUCHLINE_UPDATE_OPENAPI=1 dotnet test --filter OpenApiTests
/// </summary>
public sealed class OpenApiTests
{
    private static readonly string FilePath = Path.Combine(TestDatabase.RepoRoot(), "docs", "api", "openapi-v1.json");

    private static async Task<JsonNode> LiveDocumentAsync(TestApiFactory api)
    {
        var json = await api.CreateClient().GetStringAsync("/openapi/v1.json");
        return JsonNode.Parse(json)!;
    }

    private static string Normalise(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

    [Fact]
    public async Task The_checked_in_openapi_document_matches_the_code()
    {
        // No database is touched while generating the document.
        using var api = new TestApiFactory("Host=localhost;Database=unused;Username=unused;Password=unused", new TestTokens());
        var live = Normalise(await LiveDocumentAsync(api));
        if (Environment.GetEnvironmentVariable("TOUCHLINE_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, live);
        }
        Assert.True(File.Exists(FilePath), "docs/api/openapi-v1.json is missing. Run with TOUCHLINE_UPDATE_OPENAPI=1.");
        var stored = (await File.ReadAllTextAsync(FilePath)).Replace("\r\n", "\n");
        Assert.True(stored == live, "docs/api/openapi-v1.json is out of date. Regenerate it with TOUCHLINE_UPDATE_OPENAPI=1 and commit it.");
    }

    [Fact]
    public async Task Every_route_is_documented_and_signed_in_routes_document_the_error_contract()
    {
        using var api = new TestApiFactory("Host=localhost;Database=unused;Username=unused;Password=unused", new TestTokens());
        var doc = await LiveDocumentAsync(api);
        var paths = doc["paths"]!.AsObject();

        var routes = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.Replace(":int", ""))
            .Where(r => r.StartsWith("/api/v1/", StringComparison.Ordinal))
            .Distinct().ToList();
        Assert.All(routes, r => Assert.True(paths.ContainsKey(r), $"{r} is missing from the OpenAPI document"));

        var confirm = paths["/api/v1/fantasy/team/transfers/confirm"]!["post"]!["responses"]!.AsObject();
        foreach (var status in new[] { "400", "401", "403", "404", "409", "503" }) Assert.True(confirm.ContainsKey(status), $"confirm lacks {status}");
        Assert.Equal("stale_squad", confirm["409"]!["content"]!["application/problem+json"]!["example"]!["code"]!.GetValue<string>());
    }
}
