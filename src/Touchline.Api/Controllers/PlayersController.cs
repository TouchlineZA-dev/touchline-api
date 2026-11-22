using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/players")]
public sealed class PlayersController(FootballService football) : TouchlineControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();

        var players = await football.GetPlayersAsync(season.Id, ct);
        return Ok(new { season = new { season.Id, season.Name }, count = players.Count, players });
    }
}
