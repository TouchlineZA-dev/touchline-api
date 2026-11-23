using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/fixtures")]
public sealed class FixturesController(FootballService football, MatchDetailService details, TimeProvider time) : TouchlineControllerBase
{
    /// <param name="from">Optional ISO-8601 lower bound on kickoff, e.g. 2026-11-28T00:00:00+02:00.</param>
    /// <param name="to">Optional ISO-8601 upper bound on kickoff.</param>
    /// <param name="date">Optional calendar day in SAST (yyyy-MM-dd): kickoffs on that South African day.</param>
    /// <param name="competitionId">Optional football.competitions.id: that competition's current season (default: PSL).</param>
    /// <param name="teamId">Optional football.teams.id: only that club's fixtures.</param>
    [HttpGet, ETag]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] DateOnly? date,
        [FromQuery] int? competitionId, [FromQuery] int? teamId, CancellationToken ct)
    {
        var season = competitionId is { } id
            ? await football.GetCurrentSeasonAsync(id, ct)
            : await football.GetCurrentPslSeasonAsync(ct);
        if (season is null)
        {
            return competitionId is null
                ? NoCurrentSeason()
                : Problem(statusCode: StatusCodes.Status404NotFound, title: "No current season",
                    detail: $"Competition {competitionId} has no season flagged is_current.");
        }

        if (date is { } day)
        {
            from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), GameweekRules.Sast);
            to = from.Value.AddDays(1).AddTicks(-1);
        }

        var fixtures = await football.GetFixturesAsync(season.Id, from, to, ct, teamId);
        return Ok(new { season = new { season.Id, season.Name, season.CompetitionId }, count = fixtures.Count, fixtures, serverTimeUtc = time.GetUtcNow() });
    }

    /// <summary>
    /// One match: teams, score, phase, clock, ordered events (paged), lineups, substitutions, team stats,
    /// coverage flags, freshness and an event-set revision. Read from Postgres only; never fetches.
    /// </summary>
    [HttpGet("{id:int}"), ETag]
    public async Task<IActionResult> Get(int id, [FromQuery] int eventsPage = 1, [FromQuery] int eventsPageSize = MatchDetailService.DefaultEventsPageSize, CancellationToken ct = default)
    {
        var detail = await details.GetAsync(id, eventsPage, eventsPageSize, time.GetUtcNow(), ct);
        return detail is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Fixture {id} doesn't exist.")
            : Ok(detail);
    }
}
