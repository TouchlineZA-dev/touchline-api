using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Touchline.Api.Fantasy;

namespace Touchline.Api.Controllers;

/// <summary>
/// The 400 for a request body or query the API can't read. Same contract as rule violations (docs/api/errors.md):
/// code "invalid_request", "errors" (messages) and "violations" (rule invalid_request plus the field), so the app
/// never needs a second parser for ASP.NET's default validation shape.
/// </summary>
public static class InvalidRequest
{
    public const string Code = "invalid_request";

    public static IActionResult Create(ActionContext context)
    {
        var fields = context.ModelState
            .Where(kv => kv.Value is { Errors.Count: > 0 })
            .Select(kv => (Field: kv.Key.TrimStart('$', '.'), Messages: kv.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "This value isn't valid." : e.ErrorMessage).ToList()))
            .ToList();
        var errors = fields.SelectMany(f => f.Messages.Select(m => string.IsNullOrEmpty(f.Field) ? m : $"{f.Field}: {m}")).ToList();

        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(context.HttpContext, StatusCodes.Status400BadRequest, "The request couldn't be read",
            detail: "Fix the problems listed in 'errors'.");
        problem.Extensions["code"] = Code;
        problem.Extensions["errors"] = errors;
        problem.Extensions["violations"] = fields.SelectMany(f => f.Messages.Select(m => new { rule = Code, message = m, field = f.Field })).ToList();
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/problem+json" } };
    }
}
