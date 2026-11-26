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
    /// <summary>
    /// Liveness for the host's health check (railway.toml): 200 whenever the process is up. No database call and
    /// no freshness check, so a database outage or stale data never fails a deploy or restarts the container.
    /// </summary>
    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "live", checkedAtUtc = time.GetUtcNow() });

    /// <summary>
    /// Uptime summary. 200 "ok" when the database answers and football data is fresh; 200 "degraded" when the
    /// database answers but the data is stale (or a live fixture's poller has stalled); 503 "unavailable" only
    /// when the database can't be reached.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!await health.ReadyAsync(ct))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable", checkedAtUtc = time.GetUtcNow() });
        }
        bool fresh;
        try
        {
            fresh = (await health.FreshnessAsync(ct)).Fresh && !(await health.StalledLiveFixturesAsync(ct)).Alert;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fresh = false;   // the database answered; the freshness query didn't (details in the log)
            HttpContext.RequestServices.GetRequiredService<ILogger<HealthController>>().LogWarning(ex, "Freshness check failed during /health");
        }
        return Ok(new { status = fresh ? "ok" : "degraded", checkedAtUtc = time.GetUtcNow() });
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
