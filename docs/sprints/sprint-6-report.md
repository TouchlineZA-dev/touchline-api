# Sprint 6 report: a supported frontend API

**Branches and status**
- **touchline-api:** branch `sprint/6-frontend-contract`, stacked on `sprint/5-settlement`.
- **touchline-sync:** no migration this sprint.
- **Tests:** 408 passed, 0 skipped, 0 failed. New tests:
  - `Contract/FootballContractTests.cs` (7);
  - `Contract/LeagueAndFlowTests.cs` (7: leagues, staging kit and both end-to-end flows);
  - `Contract/OpenApiTests.cs` (2).

## "Pass when"

| Item | Evidence | Status |
|---|---|---|
| An end-to-end test using documented routes only: follow a fixture, onboard two managers into one league, make a transfer, view points, explain a rank change, with no manual DB edits by clients | `Follow_a_fixture_onboard_two_managers_share_a_league_transfer_view_points_and_explain_a_rank_change`. "Follow" means read plus ETag revalidation (304), because push follows are a proposal (decision 2). The test plays the worker (writes the match) and sets one admin flag | done |
| OpenAPI exported from the build, CI fails if stale, contract tests against real envelopes | `docs/api/openapi-v1.json`; `The_checked_in_openapi_document_matches_the_code`; `Every_route_is_documented…` (every route present; error contract with `code` examples). Envelope assertions in the contract and flow tests (exact field lists for health, standings rows and `/me`) | done (examples in `docs/api/examples.md`, not inline in the OpenAPI for each endpoint: decision 1) |
| Web compatibility: `kickoffUtc` vs `kickoff`; synthetic ratings; port `/standings`, `/player-rankings`, `/matches?fixtureId=` | `kickoff` alias served during the rollout; `docs/api/web-migration.md` (retire ratings); `/player-rankings`, `/matches?fixtureId=` (Sprint 1 `/standings`) | done |
| Player detail: id, club, position, eligibility and its source, recent minutes, upcoming fixtures, points history and reasons; duplicate names by id; club move keeps history | `GET /api/v1/players/{id}`; `Player_detail_has_identity…` (recent matches carry the club he played for then) | done |
| Club detail; paging on lists | `GET /api/v1/clubs/{id}`; `?page`/`?pageSize` on fixtures and players (all items without them, for existing clients); standings paged (Sprint 5) | done |
| Leagues: pagination, rank history, visibility, ownership transfer, archive; tests (tied ranks, concurrent joins at the cap, repeated joins, owner departure, public rank vs unpublished squad, one membership per team) | `POST /fantasy/leagues/{id}/owner`, `/archive`, `GET /fantasy/leagues/{id}/history`; `LeagueTests` (5) | done |
| CORS exposes ETag for per-environment origins | `WithExposedHeaders("ETag")`; `Cors_exposes_the_etag_to_allowed_origins_only` | done |
| Deployment: local launch target, staging/production configuration, separate DB-readiness and freshness checks | `docs/api/deployment.md`; `/health`, `/health/ready`, `/health/freshness` (minimal output) | done |
| Shared-team end-to-end (02 release gate) | `Admin_creates_a_gameweek_then_two_clients_see_the_same_team_picks_and_balance_and_a_stale_edit_gets_409` (R100.0m budget, R15.0m left) | done |
| Staging kit | `POST /api/v1/admin/staging-kit/reset`; `docs/api/staging-kit.md`; kit test | done |
| News contract proposal | `docs/api/news-proposal.md` | done (proposal; other owner) |
| Delivery proposals | `docs/api/delivery-proposal.md` | done (proposal; decision) |

## Phatu items covered this sprint

- **03 Stage 6:** the API table (account, team, squad, transfers/chips, points and rules, leagues, football, news, delivery), the OpenAPI document, the staging kit, the league tests, cache and scope rules, and the pass criteria.
- **01 "API handoff":** same as 03 Stage 6.
- **01 Gate 4:** player detail and search by id, club pages, capability flags, news contract (proposal), follows and alerts (proposal).
- **02 "Complete the public contract":**
  - every row;
  - OpenAPI from CI;
  - envelope contract tests;
  - local launch and staging/production configuration;
  - readiness and freshness monitored separately;
  - ETag for browser origins.
- **02 "Deliver the first end to end flow" §3:** the shared team.
- **02 "Release gate":** both clients read the same team id, picks and balance; a stale edit gets a recoverable conflict.

**Items I added that Part 2 didn't list:**
- **01 Gate 4:** "A club move changes future eligibility while historical match details keep the original club". Player detail shows each past match's club from that match's lineup.

## What's left

- **Sprint 7:**
  - rate limits;
  - startup configuration validation;
  - public vs authenticated caching tests;
  - access matrix;
  - load test;
  - runbooks.
- **Human:** the mobile developer runs the staging kit flow on real staging accounts.

## Needs sync team

The earlier lists stand.

## Needs other owners

1. **Web owner:** follow `docs/api/web-migration.md`:
   - switch to `kickoffUtc`, after which the `kickoff` alias is removed;
   - retire synthetic ratings;
   - handle the new status `Unknown` and `null` scores before kickoff;
   - send `gameweekId` and idempotency keys.
2. **News-service owner:** decide on `docs/api/news-proposal.md` (deployment, ingestion path, rights).
3. **Staging Supabase owner:** create the two staging reference accounts and set `StagingKit__ManagerSubjects__0/1`.
4. **Mobile owner:** pick a push provider and agree `docs/api/delivery-proposal.md`.
5. **Sportmonks subscription:** still on the free plan. (Carried over.)

## Conflicts

**Shared-team test budget:** Phatu's 02 says "Use the configured R300m starting budget". The agreed budget is **R100.0m**. The test asserts R100.0m − R85.0m = R15.0m.

## Decisions for the team

1. **Examples:**
   - Examples live in `docs/api/examples.md`, trimmed from real test responses.
   - The OpenAPI document carries the shared error responses with examples on every operation.
   - Typed per-endpoint response schemas would need `[ProducesResponseType]` on each action, which is a follow-up if the mobile codegen needs them.
2. **"Follow a fixture"** today means reading match detail and revalidating with its ETag. Push follows and alerts are the delivery proposal, which needs a dispatcher process the API doesn't run.
3. **The `kickoff` alias** stays until the web confirms the switch, then is removed in a minor version.
4. **List paging** is opt-in (`pageSize`). Without it, lists are returned whole, as before, so existing clients keep working.
5. **Health:**
   - The public `/health` now returns only `status` and time; the last sync job is no longer shown (data exposure, Sprint 7).
   - Freshness counts stale after 48 h without any football commit.
6. **The staging kit** is refused in Production regardless of settings, and returns 404 when disabled.
7. **The OpenAPI document** is served in Development and Testing, or where `OpenApi__Expose=true`. The Scalar UI is Development only.

## Migration file names

None this sprint.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/5-settlement...sprint/6-frontend-contract?expand=1 (base: `sprint/5-settlement`)
- touchline-sync: none.
