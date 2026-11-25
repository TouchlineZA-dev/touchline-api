using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Touchline.Api.Services;
using Touchline.Api.Users;

namespace Touchline.Api.Controllers;

/// <summary>The signed-in manager. Requires a Supabase access token; the token's verified "sub" is the account key.</summary>
[ApiController]
[Authorize]
[Route("api/v1/me")]
[TypeFilter(typeof(FantasyExceptionFilter))]
public sealed class MeController(CurrentUser currentUser, UserService users, AccountService accounts) : ControllerBase
{
    /// <summary>
    /// The caller's profile, created on first sign-in (repeated calls are safe). The same profile is
    /// returned for every provider linked to the same Supabase user. Email is optional. Includes the
    /// onboarding state: needsUsername, needsTeam, needsSquad (a fan can browse football without a team).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await accounts.MeAsync(await MeAsync(ct), ct));

    /// <summary>Choose or change the username, set or clear the favourite club.</summary>
    [HttpPatch, EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> Patch(UpdateMeRequest request, CancellationToken ct) =>
        Ok(await accounts.UpdateAsync(await MeAsync(ct), request, ct));

    [HttpGet("notifications")]
    public async Task<IActionResult> GetNotifications(CancellationToken ct) => Ok(await accounts.NotificationsAsync(await MeAsync(ct), ct));

    [HttpPatch("notifications"), EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> PatchNotifications(UpdateNotificationsRequest request, CancellationToken ct) =>
        Ok(await accounts.UpdateNotificationsAsync(await MeAsync(ct), request, ct));

    /// <summary>Everything stored about me, as JSON. Needs a recent sign-in.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct) => Ok(await accounts.ExportAsync(await MeAsync(ct), ct));

    /// <summary>Delete my account: personal data is anonymised, history kept. Needs a recent sign-in.</summary>
    [HttpDelete, EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> Delete(CancellationToken ct) => Ok(await accounts.DeleteAsync(await MeAsync(ct), ct));

    private async Task<Models.AppUser> MeAsync(CancellationToken ct)
    {
        if (currentUser.AuthUserId is not { } authUserId)
        {
            throw new CodedException(StatusCodes.Status401Unauthorized, "invalid_subject", "Unauthorized", "Token has no valid 'sub' claim.");
        }
        return await users.GetOrCreateAsync(authUserId, currentUser.Email, ct);
    }
}
