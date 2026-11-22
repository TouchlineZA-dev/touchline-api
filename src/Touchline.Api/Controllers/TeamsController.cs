using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/teams")]
public sealed class TeamsController(FootballService football) : TouchlineControllerBase
{
    /// <summary>Clubs playing in the current PSL season.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();

        var teams = await football.GetTeamsAsync(season.Id, ct);
        return Ok(new { season = new { season.Id, season.Name }, count = teams.Count, teams });
    }
}
