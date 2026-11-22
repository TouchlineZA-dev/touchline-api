namespace Touchline.Api.Users;

/// <summary>Bound from the "Supabase" configuration section.</summary>
public sealed class SupabaseAuthOptions
{
    public const string SectionName = "Supabase";

    /// <summary>Project URL, e.g. https://&lt;project-ref&gt;.supabase.co (no trailing path).</summary>
    public string Url { get; set; } = "";

    /// <summary>Supabase access tokens carry aud = "authenticated".</summary>
    public string Audience { get; set; } = "authenticated";

    /// <summary>
    /// Only for projects still on the legacy shared JWT secret (HS256). Leave empty when the
    /// project uses JWT signing keys; those are fetched from the public JWKS endpoint.
    /// Secret: set through user-secrets or an environment variable, never in appsettings.
    /// </summary>
    public string? JwtSecret { get; set; }

    public string Issuer => $"{Url.TrimEnd('/')}/auth/v1";

    public string JwksUrl => $"{Issuer}/.well-known/jwks.json";
}
