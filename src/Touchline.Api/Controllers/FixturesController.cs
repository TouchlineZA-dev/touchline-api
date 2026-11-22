using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/fixtures")]
public sealed class FixturesController(FootballService football) : TouchlineControllerBase
{
    /// <param name="from">Optional ISO-8601 lower bound on kickoff, e.g. 2026-11-28T00:00:00+02:00.</param>
    /// <param name="to">Optional ISO-8601 upper bound on kickoff.</param>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();

        var fixtures = await football.GetFixturesAsync(season.Id, from, to, ct);
        return Ok(new { season = new { season.Id, season.Name }, count = fixtures.Count, fixtures });
    }
}
