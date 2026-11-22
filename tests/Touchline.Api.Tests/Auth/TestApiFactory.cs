using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Touchline.Api.Users;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// The real API pipeline (Program.cs) pointed at a test database and a test Supabase project.
/// Runs as environment "Testing", so no .env, user-secrets or appsettings.Development apply.
/// </summary>
public sealed class TestApiFactory(string connectionString, TestTokens tokens) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Touchline", connectionString);
        builder.UseSetting("Supabase:Url", TestTokens.ProjectUrl);
        builder.UseSetting("Supabase:JwtSecret", "");

        builder.ConfigureTestServices(services => services
            .AddHttpClient(SupabaseSigningKeys.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubJwksHandler(tokens.JwksJson)));
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
