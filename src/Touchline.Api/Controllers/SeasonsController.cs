using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/seasons")]
public sealed class SeasonsController(FootballService football) : TouchlineControllerBase
{
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        return season is null ? NoCurrentSeason() : Ok(season);
    }
}
