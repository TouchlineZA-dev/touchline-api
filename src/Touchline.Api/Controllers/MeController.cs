using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;
using Touchline.Api.Users;

namespace Touchline.Api.Controllers;

/// <summary>The signed-in user. Requires a Supabase access token.</summary>
[ApiController]
[Authorize]
[Route("api/v1/me")]
public sealed class MeController(CurrentUser currentUser, UserService users) : ControllerBase
{
    /// <summary>Returns the caller's app.users profile, creating it on first sign-in.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (currentUser.AuthUserId is not { } authUserId)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, detail: "Token has no valid 'sub' claim.");
        }
        if (string.IsNullOrWhiteSpace(currentUser.Email))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                detail: "Token has no email claim. app.users.email is required.");
        }

        try
        {
            var user = await users.GetOrCreateAsync(authUserId, currentUser.Email, ct);
            return Ok(new { user.Id, user.Username, user.Email, authUserId });
        }
        catch (SchemaNotReadyException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Schema not ready", detail: ex.Message);
        }
        catch (UserConflictException ex)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message);
        }
    }
}
