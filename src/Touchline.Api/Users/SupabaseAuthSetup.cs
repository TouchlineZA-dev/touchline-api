using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Touchline.Api.Users;

public static class SupabaseAuthSetup
{
    /// <summary>
    /// Validates Supabase Auth access tokens sent as "Authorization: Bearer &lt;token&gt;".
    /// The user id always comes from the verified token ("sub"), never from the request body.
    /// </summary>
    public static IServiceCollection AddSupabaseAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SupabaseAuthOptions.SectionName);
        var supabase = section.Get<SupabaseAuthOptions>() ?? new SupabaseAuthOptions();

        services.Configure<SupabaseAuthOptions>(section);
        services.AddHttpClient(SupabaseSigningKeys.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<SupabaseSigningKeys>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SupabaseSigningKeys>((options, keys) =>
            {
                // Keep JWT claim names as issued ("sub", "email", "role").
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = supabase.Issuer,
                    ValidateAudience = true,
                    ValidAudience = supabase.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    IssuerSigningKeyResolver = (_, _, kid, _) => keys.GetKeys(kid),
                };
            });

        services.AddAuthorization();
        return services;
    }
}
