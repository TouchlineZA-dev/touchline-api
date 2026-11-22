using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController(HealthService health) : ControllerBase
{
    /// <summary>200 when the database answers, 503 when it does not. Includes the latest sync job.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var report = await health.CheckAsync(ct);
        var body = new
        {
            status = report.DatabaseReachable ? "ok" : "degraded",
            database = report.DatabaseReachable ? "reachable" : report.DatabaseError,
            lastSyncJob = report.LastSyncJob,
            checkedAtUtc = DateTimeOffset.UtcNow,
        };

        return report.DatabaseReachable
            ? Ok(body)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
