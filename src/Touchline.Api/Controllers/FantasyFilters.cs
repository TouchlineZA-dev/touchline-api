using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using Touchline.Api.Fantasy;
using Touchline.Api.Services;

namespace Touchline.Api.Controllers;

/// <summary>Turns service exceptions into ProblemDetails responses for the fantasy and admin routes.</summary>
public sealed class FantasyExceptionFilter(ProblemDetailsFactory problems) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var http = context.HttpContext;
        ObjectResult? result = context.Exception switch
        {
            RuleViolationException e => Problem(http, 400, e.Title, "Fix the problems listed in 'errors'.", ("errors", e.Errors), ("code", e.Code)),
            NotFoundException e => Problem(http, 404, "Not found", e.Message, ("code", "not_found")),
            ForbiddenException e => Problem(http, 403, "Forbidden", e.Message, ("code", "forbidden")),
            ConflictException e => Problem(http, 409, e.Title, e.Message,
                new (string, object?)[] { ("code", e.Code), ("current", e.Current) }.Where(x => x.Item2 is not null).Select(x => (x.Item1, x.Item2!)).ToArray()),
            // The squad version changed between reading and writing (EF concurrency token on version): another device won.
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => Problem(http, 409, "Team changed",
                "Your team changed on another device while this was being saved, so nothing was saved. Reload and try again.",
                ("code", ErrorCodes.StaleSquad)),
            // Same body as /api/v1/me: a stable "code" (legacy_email_unlinked / email_in_use) the app can act on.
            UserConflictException e => Problem(http, 409, "Account conflict", e.Message, ("code", e.Code)),
            CodedException e => Problem(http, e.Status, e.Title, e.Message, ("code", e.Code)),
            SchemaNotReadyException e => Problem(http, 503, "Schema not ready", e.Message),
            _ when Transient(context.Exception) => Problem(http, 409, "Team changed",
                "Another change to your team was being saved at the same moment, so nothing was saved. Reload and try again.",
                ("code", ErrorCodes.StaleSquad)),
            SportmonksCodesNotConfirmedException e => Problem(http, 503, "Sportmonks codes not confirmed", e.Message),
            _ when MissingSchema(context.Exception) => Problem(http, 503, "Schema not ready",
                "The database is missing a table or column the fantasy API needs. Apply the files in " +
                "docs/migrations in the Supabase SQL Editor, in number order."),
            _ => null,
        };

        if (result is not null)
        {
            context.Result = result;
            context.ExceptionHandled = true;
        }
    }

    private ObjectResult Problem(HttpContext http, int status, string title, string detail, params (string Key, object Value)[] extensions)
    {
        var problem = problems.CreateProblemDetails(http, status, title, detail: detail);
        foreach (var (key, value) in extensions) problem.Extensions[key] = value;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private static bool Transient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure }) return true;
        }
        return false;
    }

    private static bool MissingSchema(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedTable })
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Adds ETag and Cache-Control to successful GET responses and answers 304 when the client's
/// If-None-Match still matches, so the app can cache and refresh cheaply. When an action puts a
/// squad version in HttpContext.Items[SquadVersionKey], the ETag carries it ("v{version}-{hash}")
/// and the same value is accepted in If-Match on writes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ETagAttribute : Attribute, IAsyncResultFilter
{
    public const string SquadVersionKey = "touchline.squadVersion";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method)
            && context.Result is ObjectResult { Value: not null } result
            && (result.StatusCode is null or StatusCodes.Status200OK))
        {
            var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Value, result.Value.GetType(), json);
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

            var etag = http.Items[SquadVersionKey] is int version ? SquadVersion.ETag(version, hash) : $"\"h-{hash}\"";
            http.Response.Headers.ETag = etag;
            // Public football reads may be cached by shared caches; anything signed in is private to the caller
            // and never shared (Gate 9 "cache public match reads separately from authenticated responses").
            var path = http.Request.Path.Value ?? "";
            var personal = http.Request.Headers.Authorization.Count > 0
                || path.StartsWith("/api/v1/me", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/v1/fantasy", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase);
            http.Response.Headers.CacheControl = personal ? "private, no-cache" : "public, max-age=15";
            http.Response.Headers.Vary = "Authorization";

            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (ifNoneMatch.Split(',').Select(t => t.Trim()).Contains(etag))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }

        await next();
    }
}
