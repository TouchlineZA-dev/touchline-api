using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/teams")]
public sealed class TeamsController(FootballService football) : TouchlineControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var teams = await football.GetTeamsAsync(ct);
        return Ok(new { count = teams.Count, teams });
    }
}
