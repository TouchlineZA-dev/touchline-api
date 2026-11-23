using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/clubs")]
public sealed class ClubsController(FootballService football, FootballDetailService details, TimeProvider time) : TouchlineControllerBase
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

    /// <summary>One club: details, its row in the PSL table, its season fixtures and its current squad.</summary>
    [HttpGet("{id:int}"), ETag]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return NoCurrentSeason();
        var club = await details.ClubAsync(id, season, time.GetUtcNow(), ct);
        return club is null ? Problem(statusCode: 404, title: "Not found", detail: $"Club {id} isn't in the current PSL season.") : Ok(club);
    }
}
