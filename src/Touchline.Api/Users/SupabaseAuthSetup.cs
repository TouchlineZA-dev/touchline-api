using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Touchline.Api.Users;

public static class SupabaseAuthSetup
{
    /// <summary>
    /// Validates Supabase Auth access tokens sent as "Authorization: Bearer &lt;token&gt;".
    /// Google and Apple sign-in happen entirely in Supabase; whichever provider the fan used,
    /// the API only ever sees a Supabase-issued access token. The user id always comes from
    /// the verified token ("sub"), never from the request body.
    /// </summary>
    public static IServiceCollection AddSupabaseAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SupabaseAuthOptions>(configuration.GetSection(SupabaseAuthOptions.SectionName));
        services.AddHttpClient(SupabaseSigningKeys.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<SupabaseSigningKeys>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Options are read when the handler is first configured, not at registration, so
        // every configuration source (env vars, .env, user-secrets, test hosts) is applied.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SupabaseSigningKeys, IOptions<SupabaseAuthOptions>>((options, keys, supabaseOptions) =>
            {
                var supabase = supabaseOptions.Value;

                // Keep JWT claim names as issued ("sub", "email", "role").
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Issuer pins the token to this Supabase project; tokens from any other
                    // project (e.g. staging vs production) are rejected.
                    ValidateIssuer = true,
                    ValidIssuer = supabase.Issuer,
                    ValidateAudience = true,
                    ValidAudience = supabase.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    IssuerSigningKeyResolver = (_, _, kid, _) => keys.GetKeys(kid),
                };
            });

        services.AddAuthorization();
        return services;
    }
}
