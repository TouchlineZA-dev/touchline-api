using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/standings")]
public sealed class StandingsController(FootballService football, StandingsService standings, TimeProvider time) : TouchlineControllerBase
{
    /// <summary>League table for the current PSL season, or the current season of ?competitionId=.</summary>
    [HttpGet, ETag]
    public async Task<IActionResult> Get([FromQuery] int? competitionId, CancellationToken ct)
    {
        var season = competitionId is { } id ? await football.GetCurrentSeasonAsync(id, ct) : await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();
        return Ok(await standings.GetAsync(season, time.GetUtcNow(), ct));
    }
}
