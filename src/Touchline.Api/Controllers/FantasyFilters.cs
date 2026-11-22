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
            RuleViolationException e => Problem(http, 400, e.Title, "Fix the problems listed in 'errors'.", ("errors", e.Errors)),
            NotFoundException e => Problem(http, 404, "Not found", e.Message),
            ForbiddenException e => Problem(http, 403, "Forbidden", e.Message),
            ConflictException e => e.Current is null
                ? Problem(http, 409, e.Title, e.Message)
                : Problem(http, 409, e.Title, e.Message, ("current", e.Current)),
            UserConflictException e => Problem(http, 409, "Account conflict", e.Message),
            SchemaNotReadyException e => Problem(http, 503, "Schema not ready", e.Message),
            SportmonksCodesNotConfirmedException e => Problem(http, 503, "Sportmonks codes not confirmed", e.Message),
            _ when MissingSchema(context.Exception) => Problem(http, 503, "Schema not ready",
                "The database is missing a table or column the fantasy API needs. Apply docs/migrations 001 to 004 " +
                "in the Supabase SQL Editor, in number order."),
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
            http.Response.Headers.CacheControl = "private, no-cache";

            var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
            if (ifNoneMatch.Split(',').Select(t => t.Trim()).Contains(etag))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }

        await next();
    }
}
