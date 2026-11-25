using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Touchline.Api.Services;
using Touchline.Api.Users;

namespace Touchline.Api.Controllers;

/// <summary>
/// Wizard drafts: save and resume unfinished input (docs/ui/wizard-contract.md, "Drafts"). Always the
/// caller's own drafts. Kinds: onboarding, squad, lineup, transfers, league. A draft is never a saved
/// squad or entry and is never scored. 503 drafts_unavailable until migration 015 (V051) is applied.
/// </summary>
[Route("api/v1/me/drafts")]
public sealed class DraftsController(CurrentUser currentUser, UserService users, DraftService drafts)
    : FantasyControllerBase(currentUser, users)
{
    /// <summary>My live drafts (not cleared, not expired), without payloads.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await drafts.ListAsync(await MeAsync(ct), ct));

    /// <summary>One draft with its payload, or 404 draft_not_found.</summary>
    [HttpGet("{kind}")]
    public async Task<IActionResult> Get(string kind, CancellationToken ct) => Ok(await drafts.GetAsync(await MeAsync(ct), kind, ct));

    /// <summary>Save (overwrite) a draft. Send the version you last loaded (0 for a new draft); a stale version is 409 draft_stale.</summary>
    [HttpPut("{kind}"), EnableRateLimiting(RateLimitPolicies.Drafts)]
    public async Task<IActionResult> Save(string kind, SaveDraftRequest request, CancellationToken ct) =>
        Ok(await drafts.SaveAsync(await MeAsync(ct), kind, request, ct));

    /// <summary>Discard a draft (marked cleared, never deleted). Optional ?version= refuses to discard a newer draft.</summary>
    [HttpDelete("{kind}")]
    public async Task<IActionResult> Clear(string kind, [FromQuery] int? version, CancellationToken ct)
    {
        await drafts.ClearAsync(await MeAsync(ct), kind, version, ct);
        return NoContent();
    }
}
