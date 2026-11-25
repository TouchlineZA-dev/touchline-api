# Request and response examples

**Contract:**
- The full contract is `docs/api/openapi-v1.json`, exported from the build and checked in CI.
- Errors are in `docs/api/errors.md`.
- The examples below are trimmed from real responses in the test suite. Ids and times differ in each environment.
- Money is in millions of rand, and every time is UTC (`…Utc`, or `deadlineSast` for display).

## Football (public)

`GET /api/v1/fixtures/1393` (match detail; `GET /api/v1/matches?fixtureId=1393` is the same):

```json
{
  "fixture": { "id": 1393, "kickoffUtc": "2026-10-31T13:30:00+00:00", "status": "Finished", "phase": "Finished", "stateId": 5, "stateCode": "FT",
               "needsReview": false, "home": { "teamId": 50, "name": "Orlando Pirates", "score": 2 }, "away": { "teamId": 76, "name": "Kaizer Chiefs", "score": 1 },
               "lengthMinutes": 90 },
  "clock": { "minute": 90, "display": "90", "source": "Final", "running": false },
  "gameweek": { "id": 10, "number": 9, "assignmentSource": "Persisted" },
  "events": { "page": 1, "pageSize": 200, "total": 17, "items": [
    { "type": "goal", "typeId": 14, "minute": 24, "display": "24", "side": "home",
      "player": { "playerId": 812, "name": "Yanela Mbuthuma", "resolved": true },
      "relatedPlayer": { "playerId": 820, "name": "Thapelo Mokobodi", "resolved": true }, "relatedRole": "assist" } ] },
  "lineups": { "home": { "teamId": 50, "startingXi": [ ... ], "bench": [ ... ] }, "away": { ... } },
  "substitutions": [ { "display": "46", "playerOn": { "name": "Ghampani Lungu" }, "playerOff": { "name": "Nkosikhona Ndaba" } } ],
  "coverage": { "lineups": "available", "events": "available", "teamStats": "available", "playerStats": "unsupported", "unattributedEvents": 0 },
  "freshness": { "lastUpdatedUtc": "...", "serverTimeUtc": "...", "stale": false, "inMatchWindow": false, "staleAfterMinutes": 75,
                 "timestamps": { "providerTimestampUtc": null, "firstObservedUtc": "...", "dbCommittedUtc": "...", "apiPublishedUtc": "..." } },
  "revision": "r-3f9a0c1d2e4b5a6f"
}
```

**The same match in other states** (from the Sprint 1 tests):
- **Scheduled:** `phase: "Scheduled"`, both `score` `null`, `coverage.lineups: "pending"`.
- **Live:** `phase: "FirstHalf"`, `clock.source: "EstimatedFromKickoff"`, `running: true`.
- **Stale:** `freshness.stale: true` with the last commit time.
- **Postponed:** `status: "NoResult"`, `phase: "Postponed"`, coverage `not_applicable`.

## Account

`PATCH /api/v1/me` `{"username":"psl_scout"}` → `200`:

```json
{ "id": 41, "username": "psl_scout", "email": null, "authUserId": "…",
  "onboarding": { "needsUsername": false, "needsTeam": true, "needsSquad": false },
  "favouriteClubId": null, "suspended": false, "policies": { "usernameMinLength": 3, "usernameMaxLength": 20, "usernameCooldownDays": 30, … } }
```

A conflict returns `409 {"status":409,"code":"username_taken","title":"Choose another username"}`.

## Transfers

`POST /api/v1/fantasy/team/transfers/preview` `{"gameweekId":12,"out":[201],"in":[305]}`:

```json
{ "gameweekId": 12, "gameweekNumber": 11, "deadline": "2026-12-04T16:00:00+00:00", "squadVersion": 7, "priceRevision": 18,
  "quotedAtUtc": "…", "expiresAtUtc": "…", "out": [ { "playerId": 201, "price": 6.0 } ], "in": [ { "playerId": 305, "price": 6.5 } ],
  "saleProceeds": 6.0, "purchaseCost": 6.5, "bankBefore": 2.0, "bankAfter": 1.5, "freeTransfersBefore": 1, "freeTransfersAfter": 0,
  "pointsHit": 0, "gameweekPointsHit": 0, "valid": true, "violations": [] }
```

`POST /api/v1/fantasy/team/transfers/confirm` with header `Idempotency-Key: 6f1c…` and body `{"gameweekId":12,"out":[201],"in":[305],"squadVersion":7,"priceRevision":18,"quotedAtUtc":"…"}` returns a receipt:

```json
{ "receiptId": 88, "operation": "transfer_confirm", "gameweekId": 12, "squadVersionBefore": 7, "squadVersionAfter": 8,
  "transfers": [ { "playerOutId": 201, "playerInId": 305, "priceOut": 6.0, "priceIn": 6.5 } ],
  "bankBefore": 2.0, "bankAfter": 1.5, "pointsHit": 0, "replayed": false, "squad": { … } }
```

Retrying with the same key returns the same receipt with `replayed: true`.

## Points and explanations

`GET /api/v1/fantasy/team/points/explain?gameweek=11` (trimmed):

```json
{ "provisional": false, "revision": { "id": 31, "kind": "amendment", "reason": "Assist re-attributed", "correctionMessage": "Gameweek 11 scores were corrected: …" },
  "total": 49, "previousTotal": 51,
  "players": [ { "playerId": 820, "isViceCaptain": true, "multiplier": 2, "reason": "Vice-captain, ×2 (the captain played).", "points": 2,
                 "fixtures": [ { "fixtureId": 777, "items": [ { "label": "Appearance (90 min)", "points": 2 } ] } ] } ],
  "changesSincePreviousRevision": [ { "playerId": 820, "contributionBefore": 8, "contributionAfter": 4, "because": "assists 1 → 0" } ] }
```

## Leagues

`GET /api/v1/fantasy/leagues/{id}/standings?page=1&pageSize=50`:

```json
{ "leagueId": "17", "name": "Flow League", "provisional": false, "revision": "r31", "page": 1, "pageSize": 50, "totalRows": 2,
  "rows": [ { "rank": 1, "fantasyTeamId": 9, "teamName": "alice XI", "username": "alice_…", "points": 58, "overallPoints": 58, "previousRank": 2, "movement": 1 } ],
  "me": { "rank": 2, … } }
```

Standings rows show the username, team name and points only. Picks are never shown to other managers.
