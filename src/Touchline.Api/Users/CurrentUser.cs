using System.Security.Claims;

namespace Touchline.Api.Users;

public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? AuthUserId =>
        Guid.TryParse(Principal?.FindFirst("sub")?.Value, out var id) ? id : null;

    /// <summary>Optional: Apple relay, phone sign-in and Facebook without email permission have none.</summary>
    public string? Email => Principal?.FindFirst("email")?.Value is { Length: > 0 } e ? e.Trim() : null;

    /// <summary>When the token was issued ("iat"). Used to require a recent sign-in for sensitive actions.</summary>
    public DateTimeOffset? IssuedAt =>
        long.TryParse(Principal?.FindFirst("iat")?.Value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
