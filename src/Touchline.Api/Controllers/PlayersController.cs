using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/players")]
public sealed class PlayersController(FootballService football, FootballDetailService details, TimeProvider time) : TouchlineControllerBase
{
    /// <summary>Every player with position, current club, price and availability. Optional ?page and ?pageSize.</summary>
    [HttpGet, ETag]
    public async Task<IActionResult> GetAll([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();

        var all = await football.GetPlayersAsync(season.Id, ct);
        var (players, paging) = Page(all, page, pageSize);
        return Ok(new { season = new { season.Id, season.Name }, count = players.Count, players, paging });
    }

    /// <summary>
    /// One player by id: club, position, eligibility and its source, recent matches (with the club he played
    /// for then), upcoming fixtures, and fantasy points per gameweek with reasons.
    /// </summary>
    [HttpGet("{id:int}"), ETag]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();
        var player = await details.PlayerAsync(id, season, time.GetUtcNow(), ct);
        return player is null ? Problem(statusCode: 404, title: "Not found", detail: $"Player {id} doesn't exist.") : Ok(player);
    }
}
