using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Touchline.Api.Tests.Auth;

/// <summary>
/// Issues tokens shaped like Supabase Auth access tokens, signed with a throwaway ES256 key
/// (Supabase's default asymmetric signing-key algorithm). Nothing here talks to Supabase.
/// </summary>
public sealed class TestTokens
{
    public const string ProjectUrl = "https://testproject.supabase.co";
    public const string Issuer = ProjectUrl + "/auth/v1";
    public const string OtherProjectIssuer = "https://otherproject.supabase.co/auth/v1";

    private ECDsaSecurityKey _key = NewKey("test-key-1");
    private readonly List<ECDsaSecurityKey> _published = [];

    /// <summary>The JWKS document the stub serves at /auth/v1/.well-known/jwks.json (public parts only, as Supabase publishes it).</summary>
    public string JwksJson => JsonSerializer.Serialize(new
    {
        keys = _published.Select(k =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(k);
            return new { kty = jwk.Kty, crv = jwk.Crv, x = jwk.X, y = jwk.Y, kid = jwk.Kid, use = "sig", alg = SecurityAlgorithms.EcdsaSha256 };
        }).ToArray(),
    });

    public TestTokens() => _published.Add(_key);

    /// <summary>Key rotation as Supabase does it: a new signing key is published (the old one stays published) and new tokens use it.</summary>
    public void Rotate(string kid)
    {
        _key = NewKey(kid);
        _published.Add(_key);
    }

    public static ECDsaSecurityKey NewKey(string kid) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = kid };

    /// <summary>A Supabase-style access token for a user signed in with <paramref name="provider"/>.</summary>
    public string Create(
        Guid sub,
        string? email,
        string provider = "google",
        string[]? linkedProviders = null,
        string issuer = Issuer,
        string audience = "authenticated",
        DateTime? expires = null,
        SecurityKey? signWith = null,
        bool unsigned = false)
    {
        var exp = expires ?? DateTime.UtcNow.AddHours(1);
        var issuedAt = exp.AddHours(-1);

        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub.ToString(),
            ["role"] = "authenticated",
            ["aal"] = "aal1",
            ["session_id"] = Guid.NewGuid().ToString(),
            ["is_anonymous"] = false,
            ["app_metadata"] = new Dictionary<string, object>
            {
                ["provider"] = provider,
                ["providers"] = linkedProviders ?? [provider],
            },
            ["amr"] = new[] { new Dictionary<string, object> { ["method"] = "oauth", ["timestamp"] = 0 } },
        };
        if (email is not null)
        {
            claims["email"] = email;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = exp,
            Claims = claims,
            SigningCredentials = unsigned
                ? null
                : new SigningCredentials(signWith ?? _key, SecurityAlgorithms.EcdsaSha256),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }
}

/// <summary>Stands in for the Supabase JWKS endpoint so the real SupabaseSigningKeys code runs.</summary>
public sealed class StubJwksHandler(Func<string> jwks) : HttpMessageHandler
{
    public StubJwksHandler(string jwksJson) : this(() => jwksJson) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var ok = request.RequestUri?.ToString() == TestTokens.Issuer + "/.well-known/jwks.json";
        return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.NotFound)
        {
            Content = new StringContent(ok ? jwks() : "", Encoding.UTF8, "application/json"),
        });
    }
}
