using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Touchline.Api.Models;
using Touchline.Api.Services;
using Touchline.Api.Users;

namespace Touchline.Api.Controllers;

/// <summary>
/// Base for the fantasy and admin routes: requires a Supabase token, and the user always comes
/// from the token, never the request body.
/// </summary>
[ApiController]
[Authorize]
[TypeFilter(typeof(FantasyExceptionFilter))]
public abstract class FantasyControllerBase(CurrentUser currentUser, UserService users) : ControllerBase
{
    protected async Task<AppUser> MeAsync(CancellationToken ct)
    {
        if (currentUser.AuthUserId is not { } authUserId) throw new ForbiddenException("Token has no valid 'sub' claim.");
        if (string.IsNullOrWhiteSpace(currentUser.Email)) throw new RuleViolationException("Token has no email claim. app.users.email is required.");
        return await users.GetOrCreateAsync(authUserId, currentUser.Email, ct);
    }

    /// <summary>Puts the squad version into the ETag, so the app can send it back in If-Match.</summary>
    protected void UseSquadVersion(int version) => HttpContext.Items[ETagAttribute.SquadVersionKey] = version;
}

[Route("api/v1/fantasy/team")]
public sealed class FantasyTeamController(CurrentUser currentUser, UserService users, FantasyTeamService teams)
    : FantasyControllerBase(currentUser, users)
{
    /// <summary>My team for the current season (404 if none).</summary>
    [HttpGet, ETag]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await teams.GetTeamAsync(await MeAsync(ct), ct));

    /// <summary>Create my team. Only a name is needed.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateTeamRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await teams.CreateTeamAsync(await MeAsync(ct), request, ct));

    /// <summary>Everything the app needs to show the team offline. The ETag carries the squad version for If-Match.</summary>
    [HttpGet("snapshot"), ETag]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        var snapshot = await teams.SnapshotAsync(await MeAsync(ct), ct);
        if (snapshot.Squad is { Version: > 0 } squad) UseSquadVersion(squad.Version);
        return Ok(snapshot);
    }

    /// <summary>Picks, formation, captaincy and bench for a gameweek (default: the next one).</summary>
    [HttpGet("squad"), ETag]
    public async Task<IActionResult> GetSquad([FromQuery] int? gameweek, CancellationToken ct)
    {
        var squad = await teams.GetSquadAsync(await MeAsync(ct), gameweek, ct);
        if (squad.Version > 0) UseSquadVersion(squad.Version);
        return Ok(squad);
    }

    /// <summary>First full squad pick: 15 players, formation, captain, vice-captain and bench order.</summary>
    [HttpPut("squad")]
    public async Task<IActionResult> SaveSquad(SquadRequest request, CancellationToken ct) =>
        Ok(await teams.SaveSquadAsync(await MeAsync(ct), request, ct));

    /// <summary>Change formation, starters, bench or captaincy for the next gameweek. Accepts If-Match.</summary>
    [HttpPut("lineup")]
    public async Task<IActionResult> SaveLineup(
        SquadRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        Ok(await teams.SaveLineupAsync(await MeAsync(ct), request, ifMatch, ct));

    /// <summary>Transfers for the next gameweek. Returns the points cost and free transfers left. Accepts If-Match.</summary>
    [HttpPost("transfers")]
    public async Task<IActionResult> Transfers(
        TransfersRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        Ok(await teams.MakeTransfersAsync(await MeAsync(ct), request, ifMatch, ct));

    /// <summary>Play bench_boost, triple_captain or wildcard for the next gameweek.</summary>
    [HttpPost("chips")]
    public async Task<IActionResult> Chips(ChipRequest request, CancellationToken ct) =>
        Ok(await teams.ActivateChipAsync(await MeAsync(ct), request, ct));

    /// <summary>Points with a breakdown per player. Provisional (not saved) while the gameweek isn't finalised.</summary>
    [HttpGet("points"), ETag]
    public async Task<IActionResult> Points([FromQuery] int? gameweek, CancellationToken ct) =>
        Ok(await teams.GetPointsAsync(await MeAsync(ct), gameweek, ct));
}

[Route("api/v1/fantasy/leagues")]
public sealed class FantasyLeaguesController(
    CurrentUser currentUser, UserService users, FantasyContextService contexts, LeagueService leagues)
    : FantasyControllerBase(currentUser, users)
{
    /// <summary>My private leagues plus Overall, with my rank in each.</summary>
    [HttpGet, ETag]
    public async Task<IActionResult> Mine(CancellationToken ct) =>
        Ok(await leagues.MyLeaguesAsync(await ContextAsync(ct), ct));

    /// <summary>Create a private league. My team joins automatically; only I see the join code.</summary>
    [HttpPost]
    public async Task<IActionResult> Create(CreateLeagueRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await leagues.CreateAsync(await ContextAsync(ct), request, ct));

    /// <summary>Join with an exact join code.</summary>
    [HttpPost("join")]
    public async Task<IActionResult> Join(JoinLeagueRequest request, CancellationToken ct) =>
        Ok(await leagues.JoinAsync(await ContextAsync(ct), request, ct));

    /// <summary>Leave a league (the owner can't).</summary>
    [HttpPost("{id:int}/leave")]
    public async Task<IActionResult> Leave(int id, CancellationToken ct)
    {
        await leagues.LeaveAsync(await ContextAsync(ct), id, ct);
        return NoContent();
    }

    /// <summary>Standings: season totals, or one gameweek with ?gameweek=N. "overall" works as the id.</summary>
    [HttpGet("{id}/standings"), ETag]
    public async Task<IActionResult> Standings(string id, [FromQuery] int? gameweek, CancellationToken ct) =>
        Ok(await leagues.StandingsAsync(await ContextAsync(ct), id, gameweek, ct));

    private async Task<FantasyContext> ContextAsync(CancellationToken ct) =>
        await contexts.LoadAsync(await MeAsync(ct), trackTeam: false, ct);
}

/// <summary>Admin routes. Require app.users.is_admin (proposed V034).</summary>
[Route("api/v1/admin")]
public sealed class AdminController(
    CurrentUser currentUser,
    UserService users,
    AdminService admin,
    GameweekService gameweeks,
    PointsService points,
    PricingService pricing,
    TimeProvider time)
    : FantasyControllerBase(currentUser, users)
{
    [HttpPost("gameweeks")]
    public async Task<IActionResult> CreateGameweek(CreateGameweekRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        return StatusCode(StatusCodes.Status201Created, await gameweeks.CreateAsync(request, time.GetUtcNow(), ct));
    }

    /// <summary>Finalise points. Safe to run again.</summary>
    [HttpPost("gameweeks/{id:int}/score")]
    public async Task<IActionResult> Score(int id, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        return Ok(await points.FinaliseAsync(id, time.GetUtcNow(), ct));
    }

    [HttpPost("transfer-windows")]
    public async Task<IActionResult> CreateTransferWindow(CreateTransferWindowRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        return StatusCode(StatusCodes.Status201Created, await pricing.CreateWindowAsync(request, ct));
    }

    [HttpGet("transfer-windows")]
    public async Task<IActionResult> ListTransferWindows([FromQuery] int? seasonId, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        return Ok(await pricing.ListWindowsAsync(seasonId, ct));
    }

    /// <summary>Dry run of a pre-season price run for the season, plus a backtest where results exist. Saves nothing.</summary>
    [HttpPost("prices/preview")]
    public async Task<IActionResult> PreviewPrices([FromQuery] int seasonId, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        return Ok(await pricing.PreviewAsync(seasonId, ct));
    }

    /// <summary>The once-per-window price run. Refuses a second run for the same window unless force=true.</summary>
    [HttpPost("prices/recalculate")]
    public async Task<IActionResult> RecalculatePrices([FromQuery] int windowId, [FromQuery] bool force, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        return Ok(await pricing.RecalculateAsync(windowId, force, ct));
    }

    [HttpPut("players/{id:int}/price-override")]
    public async Task<IActionResult> PriceOverride(int id, PriceOverrideRequest request, CancellationToken ct)
    {
        var me = await RequireAdminAsync(ct);
        return Ok(await pricing.AddOverrideAsync(id, request, me.Id, ct));
    }

    private async Task<AppUser> RequireAdminAsync(CancellationToken ct)
    {
        var me = await MeAsync(ct);
        await admin.RequireAdminAsync(me.Id, ct);
        return me;
    }
}
