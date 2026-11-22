using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/gameweeks")]
public sealed class GameweeksController(GameweekService gameweeks) : TouchlineControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? seasonId, CancellationToken ct)
    {
        var list = await gameweeks.GetGameweeksAsync(seasonId, ct);
        return Ok(new { count = list.Count, gameweeks = list });
    }
}
