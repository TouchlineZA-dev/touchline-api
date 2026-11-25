using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

/// <summary>
/// South African football news, read only from Postgres (sync.news_cache, written by the RSS job). Public.
/// The API never calls a news provider. Every article keeps its source and links to the publisher; the
/// article text itself is never returned.
/// </summary>
[ApiController]
[Route("api/v1/news")]
public sealed class NewsController(NewsService news) : TouchlineControllerBase
{
    /// <summary>
    /// Newest first, paged (?page, ?pageSize up to 50), filtered by ?category (from /news/categories) and ?clubId
    /// (football.teams id, as in /clubs). "status" separates ok, stale (last good articles), no_articles (empty feed)
    /// and no_matches (the filter matched nothing). An unwritten feed is 503 news_unavailable.
    /// </summary>
    [HttpGet, ETag]
    public async Task<IActionResult> Get([FromQuery] string? category, [FromQuery] int? clubId, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken ct) => await Guard(async () => Ok(await news.GetAsync(category, clubId, page, pageSize, ct)));

    /// <summary>The categories the category filter accepts, with how many current articles each has.</summary>
    [HttpGet("categories"), ETag]
    public async Task<IActionResult> Categories(CancellationToken ct) => await Guard(async () => Ok(await news.CategoriesAsync(ct)));

    /// <summary>
    /// Compatibility with the web app's current /news/headlines (Netlify news.js): { totalArticles, articles, fetchedAt },
    /// newest first, ?max 1–50 (default 20). Always 200, with "unavailable": true when the feed hasn't been written.
    /// </summary>
    [HttpGet("headlines"), ETag]
    public async Task<IActionResult> Headlines([FromQuery] int? max, CancellationToken ct) => Ok(await news.HeadlinesAsync(max, ct));

    private async Task<IActionResult> Guard(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NewsUnavailableException ex)
        {
            var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status503ServiceUnavailable, "News unavailable", detail: ex.Message);
            problem.Extensions["code"] = "news_unavailable";
            return new ObjectResult(problem) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
        catch (RuleViolationException ex)
        {
            var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status400BadRequest, ex.Title, detail: string.Join(" ", ex.Errors));
            problem.Extensions["code"] = "unknown_category";
            problem.Extensions["errors"] = ex.Errors;
            return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
