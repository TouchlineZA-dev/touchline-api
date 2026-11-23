using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/clubs")]
public sealed class ClubsController(FootballService football) : TouchlineControllerBase
{
    /// <summary>
    /// Clubs in the current PSL season, with their stable internal id (football.teams.id, used for
    /// favourite club) and Sportmonks id. Public. Crest is null when only a placeholder exists.
    /// </summary>
    [HttpGet, ETag]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();
        var clubs = await football.GetClubsAsync(season.Id, ct);
        return Ok(new { season = new { season.Id, season.Name }, count = clubs.Count, clubs });
    }
}
