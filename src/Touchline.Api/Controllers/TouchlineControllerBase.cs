using Microsoft.AspNetCore.Mvc;

namespace Touchline.Api.Controllers;

// Public read endpoints over the football schema. Paths match the existing
// Netlify functions (/api/v1/players, /api/v1/fixtures) so the frontend can
// switch VITE_API_BASE_URL to this API once responses are aligned.

public abstract class TouchlineControllerBase : ControllerBase
{
    /// <summary>Same status and message as the Netlify functions when no season is current.</summary>
    protected ObjectResult NoCurrentSeason() => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "No current season",
        detail: "No current PSL season is flagged is_current in football.seasons");
}
