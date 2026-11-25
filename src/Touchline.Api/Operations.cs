using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Touchline.Api.Data;

namespace Touchline.Api;

/// <summary>Operational switches ("Operations" section), for incidents (docs/ops/release-runbook.md).</summary>
public sealed class OperationsOptions
{
    public const string SectionName = "Operations";

    /// <summary>Pauses finalising, corrections and recorded settlement transitions (503 scoring_paused). Reads and live points continue.</summary>
    public bool ScoringPaused { get; set; }

    /// <summary>Largest accepted request body, in bytes (413 above it).</summary>
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;
}

/// <summary>Rate limits per minute ("RateLimits" section). Signed-in callers are limited per user, others per IP.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int AccountChangesPerMinute { get; set; } = 10;   // username, team rename, league create/join, notifications
    public int FantasyWritesPerMinute { get; set; } = 30;    // squad, lineup, transfers, chips
    public int StandingsPerMinute { get; set; } = 60;        // league standings and history (the expensive reads)
    public int DraftSavesPerMinute { get; set; } = 60;       // wizard draft autosave (debounced in the app)
    public int WizardChecksPerMinute { get; set; } = 120;    // wizard step validation (never saves)
    public int AnonymousPerMinute { get; set; } = 300;       // everything public, per IP
}

public static class RateLimitPolicies
{
    public const string Account = "account";
    public const string FantasyWrites = "fantasy-writes";
    public const string Standings = "standings";
    public const string Drafts = "drafts";
    public const string WizardChecks = "wizard-checks";

    public static IServiceCollection AddTouchlineRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var o = config.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "https://tools.ietf.org/html/rfc6585#section-4", title = "Too many requests", status = 429,
                    detail = "Slow down and try again in a minute.", code = "rate_limited",
                }), ct);
            };
            limiter.AddPolicy(Account, ctx => PerCaller(ctx, o.AccountChangesPerMinute));
            limiter.AddPolicy(FantasyWrites, ctx => PerCaller(ctx, o.FantasyWritesPerMinute));
            limiter.AddPolicy(Standings, ctx => PerCaller(ctx, o.StandingsPerMinute));
            limiter.AddPolicy(Drafts, ctx => PerCaller(ctx, o.DraftSavesPerMinute));
            limiter.AddPolicy(WizardChecks, ctx => PerCaller(ctx, o.WizardChecksPerMinute));
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.Request.Headers.Authorization.Count > 0
                    ? RateLimitPartition.GetNoLimiter("signed-in")
                    : RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress, _ => Window(o.AnonymousPerMinute)));
        });
    }

    private static RateLimitPartition<string> PerCaller(HttpContext ctx, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirst("sub")?.Value is { } sub ? "user:" + sub : "ip:" + ctx.Connection.RemoteIpAddress,
            _ => Window(perMinute));

    private static FixedWindowRateLimiterOptions Window(int perMinute) =>
        new() { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
}

/// <summary>
/// Refuses incomplete or unsafe configuration at startup in Staging and Production (03 Stage 7 "make startup
/// reject incomplete production configuration"). Messages name the setting, never its value.
/// </summary>
public static class StartupValidation
{
    public static IReadOnlyList<string> Problems(IConfiguration config, IHostEnvironment env)
    {
        var problems = new List<string>();
        var cs = config.GetConnectionString("Touchline");
        if (string.IsNullOrWhiteSpace(cs))
        {
            problems.Add("ConnectionStrings:Touchline is not set.");
        }
        else
        {
            try
            {
                var b = new NpgsqlConnectionStringBuilder(cs);
                if (b.SslMode is not (SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull)) problems.Add("ConnectionStrings:Touchline must use SSL Mode=Require (or stricter).");
                if (string.IsNullOrWhiteSpace(b.Username) || b.Username.Split('.')[0] is "postgres" or "supabase_admin" or "sync_worker_user")
                {
                    problems.Add("ConnectionStrings:Touchline must connect as the scoped API role (app_api_user), not an owner or the worker role.");
                }
            }
            catch (ArgumentException)
            {
                problems.Add("ConnectionStrings:Touchline can't be parsed.");
            }
        }

        var url = config["Supabase:Url"];
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var supabase) || supabase.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("Supabase:Url must be the environment's https Supabase project URL.");
        }
        var secret = config["Supabase:JwtSecret"];
        if (!string.IsNullOrEmpty(secret) && secret.Length < 32) problems.Add("Supabase:JwtSecret is too short (32+ characters) or should be empty when using signing keys.");

        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0) problems.Add("Cors:AllowedOrigins must list the environment's web origins.");
        if (origins.Any(o => o.Contains('*') || !o.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("Cors:AllowedOrigins must be explicit https origins (no wildcards, no http).");
        }

        if (env.IsProduction() && config.GetValue<bool>("StagingKit:Enabled")) problems.Add("StagingKit:Enabled must be false in Production.");
        return problems;
    }

    public static void ThrowIfInvalid(IConfiguration config, IHostEnvironment env)
    {
        if (!env.IsProduction() && !env.IsStaging()) return;
        var problems = Problems(config, env);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Refusing to start in {env.EnvironmentName}: " + string.Join(" ", problems));
        }
    }
}

/// <summary>
/// Audit log for privileged operations (03 Stage 7 "keep actor, timestamp, reason and outcome"): every
/// admin route call is written to app.admin_audit_log (migration 012) with the actor, the action and its
/// target, the reason from the request body when it has one, and the outcome status. A failure to write
/// the audit row is logged, never turned into a failed request. Before 012 nothing is written.
/// </summary>
public sealed class AdminAuditFilter(IServiceScopeFactory scopes, SchemaFeatures schema, ILogger<AdminAuditFilter> logger) : IAsyncActionFilter, IAsyncAuthorizationFilter
{
    public const string UserIdKey = "touchline.userId";

    /// <summary>
    /// The admin check runs as an authorization filter, before model binding and validation, so a
    /// non-admin always gets 403 (never a 400 that reveals the route's body rules). Refusals are audited too.
    /// </summary>
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true) return;   // [Authorize] answers 401
        var current = http.RequestServices.GetRequiredService<Users.CurrentUser>();
        if (current.AuthUserId is not { } sub) return;
        try
        {
            var user = await http.RequestServices.GetRequiredService<Users.UserService>().GetOrCreateAsync(sub, current.Email, http.RequestAborted);
            http.Items[UserIdKey] = user.Id;
            await http.RequestServices.GetRequiredService<Services.AdminService>().RequireAdminAsync(user.Id, http.RequestAborted);
        }
        catch (Services.ForbiddenException ex)
        {
            context.Result = new ObjectResult(new { type = "https://tools.ietf.org/html/rfc9110#section-15.5.4", title = "Forbidden", status = 403, detail = ex.Message, code = "forbidden" })
            { StatusCode = StatusCodes.Status403Forbidden };
            await WriteAsync(http, $"{http.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template}",
                JsonSerializer.Serialize(context.RouteData.Values.Where(v => v.Key is not ("controller" or "action")).ToDictionary(v => v.Key, v => v.Value?.ToString())),
                null, StatusCodes.Status403Forbidden);
        }
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        try
        {
            var http = context.HttpContext;
            var status = executed.Exception is not null && !executed.ExceptionHandled
                ? 500
                : executed.Result switch
                {
                    IStatusCodeActionResult { StatusCode: { } code } => code,
                    _ => http.Response.StatusCode,
                };
            var reason = context.ActionArguments.Values
                .Select(v => v?.GetType().GetProperty("Reason")?.GetValue(v) as string)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
            var target = JsonSerializer.Serialize(context.ActionArguments
                .Where(a => a.Value is int or long or string or bool)
                .ToDictionary(a => a.Key, a => a.Value));
            await WriteAsync(http, $"{http.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template}", target, reason, status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write the admin audit row");
        }
    }

    private async Task WriteAsync(HttpContext http, string action, string target, string? reason, int status)
    {
        try
        {
            if (!await schema.HasTableAsync("app", "admin_audit_log")) return;
            int? actor = http.Items[UserIdKey] is int id ? id : null;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TouchlineDbContext>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO app.admin_audit_log (actor_user_id, action, target, reason, outcome_status)
                VALUES ({actor}, {action}, CAST({target} AS jsonb), {reason}, {status})
                """);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write the admin audit row");
        }
    }
}
