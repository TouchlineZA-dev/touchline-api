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
                Add(operation, "400", "Validation failed (code: rule_violation, invalid_request, target_gameweek_required, insufficient_funds, username_cooldown, ...). " +
                    "'violations' names each broken rule (docs/api/errors.md, Rule names).", "rule_violation", 400, "Some rules aren't met",
                    e => e["violations"] = new JsonArray(new JsonObject { ["rule"] = "club_limit", ["message"] = "Max 3 players from Orlando Pirates; you have 4." }));
                if (authed)
                {
                    Add(operation, "401", "No usable token. code token_expired (retry: refresh): refresh the Supabase session and retry once. " +
                        "code token_missing or token_invalid (retry: sign_in): sign in again.", "token_expired", 401, "Session expired",
                        e => e["retry"] = "refresh");
                    Add(operation, "403", "Not allowed (code: forbidden, account_suspended, account_deleted, reauthentication_required)", "forbidden", 403, "Forbidden");
                    Add(operation, "409", "Conflict (code: stale_squad, gameweek_locked, price_changed, quote_expired, idempotency_key_reused, username_taken, " +
                        "team_exists, league_full, already_member, draft_stale, ...). 'current' holds the latest state when there is one.", "stale_squad", 409, "Team changed");
                    Add(operation, "429", "Too many requests for this manager in a minute (code: rate_limited). Wait and retry; the session is fine.", "rate_limited", 429, "Too many requests");
                }
                Add(operation, "404", "Not found (code: not_found, draft_not_found)", "not_found", 404, "Not found");
                Add(operation, "503", "Data or schema not available (code: drafts_unavailable for drafts before migration 015)", null, 503, "Schema not ready");
                WizardExamples.Apply(operation, path, context.Description.HttpMethod ?? "");
                return Task.CompletedTask;
            });
        });

    private static void Add(OpenApiOperation operation, string status, string description, string? code, int statusCode, string title,
        Action<JsonObject>? extra = null)
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
        extra?.Invoke(example);
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
