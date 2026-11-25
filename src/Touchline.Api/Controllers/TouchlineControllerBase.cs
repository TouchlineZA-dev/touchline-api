using Microsoft.AspNetCore.Mvc;

namespace Touchline.Api.Controllers;

// Public read endpoints over the football schema. Paths match the existing
// Netlify functions (/api/v1/players, /api/v1/fixtures) so the frontend can
// switch VITE_API_BASE_URL to this API once responses are aligned.

public abstract class TouchlineControllerBase : ControllerBase
{
    public const int MaxPageSize = 500;

    /// <summary>
    /// Optional paging for public lists. Without pageSize the whole list is returned, as before
    /// (existing clients); with it, one page plus the total.
    /// </summary>
    protected static (IReadOnlyList<T> Items, object Paging) Page<T>(IReadOnlyList<T> all, int? page, int? pageSize)
    {
        if (pageSize is null) return (all, new { page = 1, pageSize = all.Count, total = all.Count });
        var size = Math.Clamp(pageSize.Value, 1, MaxPageSize);
        var p = Math.Max(1, page ?? 1);
        return (all.Skip((p - 1) * size).Take(size).ToList(), new { page = p, pageSize = size, total = all.Count });
    }

    /// <summary>Same status and message as the Netlify functions when no season is current.</summary>
    protected ObjectResult NoCurrentSeason() => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "No current season",
        detail: "No current PSL season is flagged is_current in football.seasons");
}
