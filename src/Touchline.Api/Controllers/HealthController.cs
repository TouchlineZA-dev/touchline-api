using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

/// <summary>
/// Health, split three ways (02 "Monitor database readiness and data freshness separately"). Public
/// output is minimal: no hosts, versions, job names or error text (those go to the server log).
/// </summary>
[ApiController]
[Route("api/v1/health")]
public sealed class HealthController(HealthService health, TimeProvider time) : ControllerBase
{
    /// <summary>Liveness plus database reachability. 200 "ok" or 503 "degraded".</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var ready = await health.ReadyAsync(ct);
        var body = new { status = ready ? "ok" : "degraded", checkedAtUtc = time.GetUtcNow() };
        return ready ? Ok(body) : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }

    /// <summary>Readiness for a load balancer: 200 when the database answers, 503 otherwise.</summary>
    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct) =>
        await health.ReadyAsync(ct) ? Ok(new { ready = true }) : StatusCode(StatusCodes.Status503ServiceUnavailable, new { ready = false });

    /// <summary>
    /// Data freshness, separate from readiness: a healthy API with a stopped feed still needs attention.
    /// Always 200 (the API is serving); "fresh" says whether football data arrived recently.
    /// </summary>
    [HttpGet("freshness")]
    public async Task<IActionResult> Freshness(CancellationToken ct)
    {
        var report = await health.FreshnessAsync(ct);
        var stalled = await health.StalledLiveFixturesAsync(ct);
        return Ok(new { fresh = report.Fresh && !stalled.Alert, lastFootballCommitUtc = report.LastFootballCommitUtc, ageMinutes = report.AgeMinutes,
            staleAfterHours = report.StaleAfterHours, stalledLiveFixtures = stalled.Count, checkedAtUtc = time.GetUtcNow() });
    }
}
