using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Touchline.Api.Users;

/// <summary>
/// Every 401 from the token check says why, so the app can tell "refresh the session and retry" from
/// "sign in again" instead of logging the manager out on any error (docs/ui/wizard-contract.md, "Sessions").
/// - token_missing: no Authorization header. Sign in (or attach the token).
/// - token_expired: a real token from this project that has expired. Refresh it with Supabase and retry once.
/// - token_invalid: wrong project, wrong audience, bad signature or malformed. Sign in again.
/// The WWW-Authenticate header follows RFC 6750 (error="invalid_token" for expired and invalid tokens).
/// </summary>
public static class AuthChallenge
{
    public const string TokenMissing = "token_missing";
    public const string TokenExpired = "token_expired";
    public const string TokenInvalid = "token_invalid";

    public static async Task WriteAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var hasToken = context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        var expired = IsExpired(context.AuthenticateFailure);

        var (code, title, detail, retry, header) = (hasToken, expired) switch
        {
            (false, _) => (TokenMissing, "Sign-in required", "Send the Supabase access token as 'Authorization: Bearer <token>'.", "sign_in",
                "Bearer"),
            (true, true) => (TokenExpired, "Session expired", "The access token has expired. Refresh the Supabase session and retry the request.", "refresh",
                "Bearer error=\"invalid_token\", error_description=\"The access token expired\""),
            _ => (TokenInvalid, "Invalid token", "The access token isn't valid for this API. Sign in again.", "sign_in",
                "Bearer error=\"invalid_token\", error_description=\"The access token is invalid\""),
        };

        var response = context.Response;
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = header;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "application/problem+json";
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
            ["title"] = title,
            ["status"] = 401,
            ["detail"] = detail,
            ["code"] = code,
            ["retry"] = retry,
            ["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier,
        };
        await response.WriteAsync(JsonSerializer.Serialize(body), context.HttpContext.RequestAborted);
    }

    private static bool IsExpired(Exception? failure) => failure switch
    {
        null => false,
        SecurityTokenExpiredException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsExpired),
        _ => IsExpired(failure.InnerException),
    };
}
