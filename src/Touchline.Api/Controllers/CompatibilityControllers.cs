using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

/// <summary>Port of the web app's Netlify /player-rankings function (02 "Complete the public contract").</summary>
[ApiController]
[Route("api/v1/player-rankings")]
public sealed class PlayerRankingsController(FootballDetailService details) : TouchlineControllerBase
{
    /// <summary>Top 10 for goals, assists and clean sheets. ?competitionId and ?seasonId accept internal or Sportmonks ids, as before.</summary>
    [HttpGet, ETag]
    public async Task<IActionResult> Get([FromQuery] int? competitionId, [FromQuery] int? seasonId, CancellationToken ct)
    {
        var rankings = await details.RankingsAsync(competitionId, seasonId, ct);
        return rankings is null
            ? Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data", detail: "No competition or season is available.")
            : Ok(rankings);
    }
}

/// <summary>The web app's /matches?fixtureId= (Netlify), served by the match-detail endpoint.</summary>
[ApiController]
[Route("api/v1/matches")]
public sealed class MatchesController(MatchDetailService details, TimeProvider time) : TouchlineControllerBase
{
    /// <summary>Same response as GET /api/v1/fixtures/{id}. Kept for the web rollout (docs/api/web-migration.md).</summary>
    [HttpGet, ETag]
    public async Task<IActionResult> Get([FromQuery] int? fixtureId, CancellationToken ct)
    {
        if (fixtureId is null) return Problem(statusCode: 400, title: "fixtureId required", detail: "Send ?fixtureId=.");
        var detail = await details.GetAsync(fixtureId.Value, 1, MatchDetailService.DefaultEventsPageSize, time.GetUtcNow(), ct);
        return detail is null ? Problem(statusCode: 404, title: "Not found", detail: $"Fixture {fixtureId} doesn't exist.") : Ok(detail);
    }
}
