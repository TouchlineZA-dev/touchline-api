using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Touchline.Api;

/// <summary>
/// OpenAPI document (docs/api/openapi-v1.json is exported from it and checked in CI). Adds the API's
/// shared error responses — RFC 9110 problem details with a machine-readable "code" — to every
/// operation, with an example each, so clients see the error contract next to the success shape.
/// </summary>
public static class OpenApiSetup
{
    public static IServiceCollection AddTouchlineOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(o =>
        {
            o.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Touchline API",
                    Version = "v1",
                    Description = "PSL football facts and the Touchline fantasy game. Errors: docs/api/errors.md. Examples: docs/api/examples.md.",
                };
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((operation, context, _) =>
            {
                var path = context.Description.RelativePath ?? "";
                var authed = path.StartsWith("api/v1/me", StringComparison.Ordinal) || path.StartsWith("api/v1/fantasy", StringComparison.Ordinal)
                             || path.StartsWith("api/v1/admin", StringComparison.Ordinal);
                operation.Responses ??= new OpenApiResponses();
                Add(operation, "400", "Validation failed (code: rule_violation, target_gameweek_required, insufficient_funds, …)", "rule_violation", 400, "Some rules aren't met");
                if (authed)
                {
                    Add(operation, "401", "Missing, expired or invalid Supabase token", "invalid_subject", 401, "Unauthorized");
                    Add(operation, "403", "Not allowed (code: forbidden, account_suspended, account_deleted, reauthentication_required)", "forbidden", 403, "Forbidden");
                    Add(operation, "409", "Conflict (code: stale_squad, gameweek_locked, price_changed, quote_expired, idempotency_key_reused, username_taken, …)", "stale_squad", 409, "Team changed");
                }
                Add(operation, "404", "Not found (code: not_found)", "not_found", 404, "Not found");
                Add(operation, "503", "Data or schema not available", null, 503, "Schema not ready");
                return Task.CompletedTask;
            });
        });

    private static void Add(OpenApiOperation operation, string status, string description, string? code, int statusCode, string title)
    {
        if (operation.Responses!.ContainsKey(status)) return;
        var example = new JsonObject
        {
            ["type"] = "https://tools.ietf.org/html/rfc9110",
            ["title"] = title,
            ["status"] = statusCode,
            ["detail"] = "Human-readable explanation.",
        };
        if (code is not null) example["code"] = code;
        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType { Example = example },
            },
        };
    }
}
