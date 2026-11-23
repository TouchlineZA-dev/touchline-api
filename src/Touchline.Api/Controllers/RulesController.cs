using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Controllers;

[ApiController]
[Route("api/v1/rules")]
public sealed class RulesController(IOptions<FantasyRules> rules) : ControllerBase
{
    /// <summary>The season rulebook (version "2026-27.v1"), machine-readable. Public; managers can read it before picking a squad.</summary>
    [HttpGet, ETag]
    public IActionResult Get() => Ok(Rulebook.Build(rules.Value));
}
