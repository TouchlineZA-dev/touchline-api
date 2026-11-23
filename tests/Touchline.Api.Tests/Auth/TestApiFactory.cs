using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Touchline.Api.Users;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// The real API pipeline (Program.cs) pointed at a test database and a test Supabase project.
/// Runs as environment "Testing", so no .env, user-secrets or appsettings.Development apply.
/// Every outbound HTTP request goes through <see cref="OutboundGuard"/>: only the Supabase JWKS
/// endpoint is allowed (architecture rule: the API never calls Sportmonks or the sync worker).
/// </summary>
public sealed class TestApiFactory(string connectionString, TestTokens tokens, TimeProvider? time = null, IReadOnlyDictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    public OutboundGuard Outbound { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Touchline", connectionString);
        builder.UseSetting("Supabase:Url", TestTokens.ProjectUrl);
        builder.UseSetting("Supabase:JwtSecret", "");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>()) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler(() => new OutboundGuardHandler(Outbound)));
            services.AddHttpClient(SupabaseSigningKeys.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubJwksHandler(tokens.JwksJson));
            if (time is not null) services.Replace(ServiceDescriptor.Singleton(time));
        });
    }

    public HttpClient ClientWithToken(string? token)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }
}

/// <summary>Records every outbound request the API makes; anything but Supabase JWKS is refused.</summary>
public sealed class OutboundGuard
{
    public const string AllowedHost = "testproject.supabase.co";
    public const string AllowedPath = "/auth/v1/.well-known/jwks.json";

    public ConcurrentQueue<Uri> Allowed { get; } = new();
    public ConcurrentQueue<Uri> Refused { get; } = new();
}

public sealed class OutboundGuardHandler(OutboundGuard guard) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == OutboundGuard.AllowedHost && uri.AbsolutePath == OutboundGuard.AllowedPath)
        {
            guard.Allowed.Enqueue(uri);
            return base.SendAsync(request, cancellationToken);
        }
        guard.Refused.Enqueue(uri);
        throw new InvalidOperationException($"Architecture rule broken: the API tried to call {uri.Host}. Only Supabase JWKS is allowed.");
    }
}

/// <summary>A clock tests can move.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}
