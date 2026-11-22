using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// Token checks on GET /api/v1/me. Every case here is rejected by authentication before the
/// controller runs, so no database is needed (the connection string points nowhere).
/// </summary>
public sealed class TokenValidationTests : IClassFixture<TokenValidationTests.Api>, IDisposable
{
    public sealed class Api : IDisposable
    {
        public TestTokens Tokens { get; } = new();
        public TestApiFactory Factory { get; }
        public Api() => Factory = new TestApiFactory("Host=127.0.0.1;Port=1;Database=unused;Timeout=1", Tokens);
        public void Dispose() => Factory.Dispose();
    }

    private readonly Api _api;
    public TokenValidationTests(Api api) => _api = api;
    public void Dispose() { }

    private Task<HttpResponseMessage> GetMe(string? token) => _api.Factory.ClientWithToken(token).GetAsync("/api/v1/me");

    [Fact]
    public async Task Missing_token_is_401()
    {
        var response = await GetMe(null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_token_is_401()
    {
        var response = await GetMe("not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_by_unknown_key_is_401()
    {
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com", signWith: TestTokens.NewKey("attacker-key"));
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_by_unknown_key_reusing_a_real_kid_is_401()
    {
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com", signWith: TestTokens.NewKey("test-key-1"));
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_payload_is_401()
    {
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com");
        var parts = token.Split('.');
        var forged = _api.Tokens.Create(Guid.NewGuid(), "victim@example.com").Split('.')[1];
        var response = await GetMe($"{parts[0]}.{forged}.{parts[2]}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_401()
    {
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com", unsigned: true);
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        // Past the default 5-minute clock skew.
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com", expires: DateTime.UtcNow.AddMinutes(-10));
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("expired", response.Headers.WwwAuthenticate.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_from_another_supabase_project_is_401()
    {
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com", issuer: TestTokens.OtherProjectIssuer);
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("anon")]
    [InlineData("service_role")]
    [InlineData("something-else")]
    public async Task Token_with_wrong_audience_is_401(string audience)
    {
        var token = _api.Tokens.Create(Guid.NewGuid(), "fan@example.com", audience: audience);
        var response = await GetMe(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Only_me_requires_authentication()
    {
        // Public football endpoints must stay reachable without a token.
        var endpoints = _api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        var protectedRoutes = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .Distinct()
            .ToList();

        Assert.Equal(["api/v1/me"], protectedRoutes);
        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "api/v1/teams");
        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "api/v1/players");
        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "api/v1/fixtures");

        var auth = _api.Factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        Assert.Null(auth.FallbackPolicy);
    }
}
