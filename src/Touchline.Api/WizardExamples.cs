using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Touchline.Api;

/// <summary>Request and response examples for the wizard and draft routes in the OpenAPI document (docs/ui/wizard-contract.md).</summary>
internal static class WizardExamples
{
    public static void Apply(OpenApiOperation operation, string path, string method)
    {
        var (request, response) = (path, method.ToUpperInvariant()) switch
        {
            ("api/v1/fantasy/team/squad/validate", "POST") => (SquadRequest(), SquadProgress()),
            ("api/v1/fantasy/team/lineup/validate", "POST") => (LineupRequest(), LineupProgress()),
            ("api/v1/fantasy/team/transfers/validate", "POST") => (TransferRequest(), TransferProgress()),
            ("api/v1/me/drafts/{kind}", "PUT") => (DraftSave(), Draft()),
            ("api/v1/me/drafts/{kind}", "GET") => ((JsonNode?)null, Draft()),
            ("api/v1/me/drafts", "GET") => (null, DraftList()),
            _ => (null, null),
        };
        if (request is not null && operation.RequestBody?.Content is { } content)
        {
            foreach (var media in content.Values) media.Example = request;
        }
        if (response is not null)
        {
            operation.Responses ??= new OpenApiResponses();
            operation.Responses["200"] = new OpenApiResponse
            {
                Description = "OK",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Example = response } },
            };
        }
    }

    private static JsonNode SquadRequest() => JsonNode.Parse("""{"players":[101,102,201,202,203,204,401]}""")!;

    private static JsonNode SquadProgress() => JsonNode.Parse("""
        {"kind":"squad","complete":false,"valid":false,
         "budget":{"total":100.0,"spent":33.5,"left":66.5,"overBudget":false},
         "positions":[{"position":"GK","have":2,"need":2},{"position":"DEF","have":4,"need":5},{"position":"MID","have":0,"need":5},{"position":"ATT","have":1,"need":3}],
         "clubs":[{"clubId":12,"club":"Orlando Pirates","count":3,"max":3,"overLimit":false}],
         "formation":null,"lineup":null,"captaincy":null,"transfers":null,
         "violations":[],"warnings":[],
         "nextSteps":["Pick 1 more defender.","Pick 5 more midfielders.","Pick 2 more attackers."],
         "gameweekId":10,"serverTimeUtc":"2026-11-25T18:00:00+00:00"}
        """)!;

    private static JsonNode LineupRequest() => JsonNode.Parse("""
        {"formation":"3-5-2","starters":[101,201,202,203,301,302,303,304,305,401,402],"bench":[102,204,205,403],"captainId":401,"viceCaptainId":301}
        """)!;

    private static JsonNode LineupProgress() => JsonNode.Parse("""
        {"kind":"lineup","complete":true,"valid":true,
         "budget":{"total":100.0,"spent":96.5,"left":3.5,"overBudget":false},
         "positions":[{"position":"GK","have":2,"need":2},{"position":"DEF","have":5,"need":5},{"position":"MID","have":5,"need":5},{"position":"ATT","have":3,"need":3}],
         "clubs":[],
         "formation":{"requested":"3-5-2","actual":"3-5-2","valid":true,"allowed":["4-3-3","4-4-2","3-5-2","3-4-3","5-3-2"]},
         "lineup":{"startersHave":11,"startersNeed":11,"startingGoalkeepers":1,"benchHave":4,"benchNeed":4,"benchGoalkeepers":1,"valid":true},
         "captaincy":{"captainId":401,"viceCaptainId":301,"valid":true},"transfers":null,
         "violations":[],"warnings":[],"nextSteps":[],"gameweekId":10,"serverTimeUtc":"2026-11-25T18:00:00+00:00"}
        """)!;

    private static JsonNode TransferRequest() => JsonNode.Parse("""{"out":[203],"in":[]}""")!;

    private static JsonNode TransferProgress() => JsonNode.Parse("""
        {"kind":"transfers","complete":false,"valid":false,
         "budget":{"total":100.0,"spent":91.5,"left":8.5,"overBudget":false},
         "positions":[{"position":"GK","have":2,"need":2},{"position":"DEF","have":4,"need":5},{"position":"MID","have":5,"need":5},{"position":"ATT","have":3,"need":3}],
         "clubs":[],"formation":null,"lineup":null,"captaincy":null,
         "transfers":{"out":1,"in":0,"unlimited":false,"freeTransfersBefore":1,"freeTransfersAfter":0,"pointsHit":0,"gameweekPointsHit":0,"saleProceeds":5.0,"purchaseCost":0.0},
         "violations":[],"warnings":[],"nextSteps":["Buy 1 defender."],"gameweekId":10,"serverTimeUtc":"2026-11-25T18:00:00+00:00"}
        """)!;

    private static JsonNode DraftSave() => JsonNode.Parse("""{"payload":{"players":[101,102,201]},"version":3,"step":"pick_defenders"}""")!;

    private static JsonNode Draft() => JsonNode.Parse("""
        {"kind":"squad","step":"pick_defenders","payload":{"players":[101,102,201]},"version":4,
         "createdAtUtc":"2026-11-25T17:40:00+00:00","updatedAtUtc":"2026-11-25T18:00:00+00:00","expiresAtUtc":"2026-12-09T18:00:00+00:00"}
        """)!;

    private static JsonNode DraftList() => JsonNode.Parse("""
        {"drafts":[{"kind":"squad","step":"pick_defenders","version":4,"updatedAtUtc":"2026-11-25T18:00:00+00:00","expiresAtUtc":"2026-12-09T18:00:00+00:00"}],
         "expiryDays":14,"serverTimeUtc":"2026-11-25T18:05:00+00:00"}
        """)!;
}
