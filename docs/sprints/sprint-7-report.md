# Sprint 7 report: operate and secure

**Branches and status**
- **touchline-api:** branch `sprint/7-operate-secure`, stacked on `sprint/6-frontend-contract`.
- **touchline-sync:** branch `sprint/7-migrations`, stacked on `sprint/5-migrations` (Sprint 6 had no migration).
- **Tests:** 415 passed, 0 skipped, 0 failed (local PostgreSQL 17). New: `Security/SecurityTests.cs` (7, including the full GW9 replay through the public API).

## "Pass when" (code side)

| Item | Evidence | Status |
|---|---|---|
| CI runs every DB test, the dependency check and secret scanning | `.github/workflows/ci.yml`:<br>• restore, build and test in Release with `TOUCHLINE_REQUIRE_DB_TESTS=true`;<br>• the test result recorded against `GITHUB_SHA` in the job summary and as an artifact;<br>• `dotnet list package --vulnerable --include-transitive` fails the build (none found today);<br>• a gitleaks job with `.gitleaks.toml` | done; CI hasn't run on these branches yet, as it runs when the PRs are opened |
| The access-matrix tests pass | `Access_matrix_anonymous_gets_401_managers_get_403_on_every_admin_route_and_each_call_is_audited` (every admin route: anonymous 401, manager 403, each refusal audited); `TokenValidationTests` route classification; league, account and safe-write cross-user tests | done |
| Startup rejects bad production config | `StartupValidation` (Staging and Production): SSL, scoped role, https Supabase URL, JWT secret length, explicit https CORS origins, staging kit off in Production. `Production_startup_rejects_incomplete_or_unsafe_configuration` | done |
| The load script, runbooks and release record exist | `docs/load/k6-deadline.js` + README; `docs/ops/release-runbook.md` (restore, rollback, rotation, incidents, independent switches); `docs/ops/matchday-runbook.md`; `docs/release/release-record.md` (pre-filled) | done |
| Postgres cases: concurrent squad mutations, deadline freezes, settlement interruption, duplicate retries; full gameweek replay checking the public API | Sprint 4 (`SafeWriteTests`), Sprint 5 (`An_interrupted_run_resumes…`); `GameweekReplayTests.Gw9_settled_through_the_api_publishes_the_reference_totals` (real GW9, reference squads, admin settlement, public standings 65 / 61) | done |
| Identity: pinned issuer and audience, signature, expiry; tests for expired tokens, JWKS rotation and suspension | `TokenValidationTests`; `A_rotated_signing_key_is_picked_up_from_jwks_and_old_tokens_keep_working`; the suspension test (Sprint 3) | done |
| Access matrix doc + SQL for effective permissions of the direct Supabase roles, with its output | `docs/security/access-matrix.md`, `docs/security/effective-permissions.sql`. Live output (read-only, 2026-11-23) is summarised in the matrix. **Finding:** the worker role has more `app` access than designed | done (finding → needs sync team) |
| Config and secrets: production rejects incomplete config; staging and production kept separate; no secrets in code or logs; rotation runbook | `StartupValidation` (problems name settings, never values); `docs/api/deployment.md`; secret scan; release runbook § rotation | done |
| Data exposure: public reads cached separately; squads, invite codes, emails and device tokens never in public or cached responses; minimal public health | `public, max-age=15` for public reads and `private, no-cache` + `Vary: Authorization` for signed-in ones; `Public_reads_are_cached_separately_and_never_carry_emails_join_codes_or_squads`; minimal `/health` (Sprint 6). There are no device tokens yet | done |
| Rate and size limits | `RateLimitPolicies`:<br>• account changes 10/min per user;<br>• fantasy writes 30/min per user;<br>• standings 60/min per user;<br>• anonymous 300/min per IP.<br>Bodies over 64 KB get 413. `Account_changes_are_rate_limited_per_user_and_oversized_bodies_are_refused` | done |
| Audit log: actor, time, reason, outcome for scoring, pricing, calendar, corrections, admin resets, manual sync | `app.admin_audit_log` (migration 012); `AdminAuditFilter` on every admin route, including refusals; `Admin_actions_are_audited…`. The API has no manual-sync route (rule 1) | done |
| Privacy inventory linked to export, correction, deletion and opt-out; backups and history; POPIA flag | `docs/security/privacy-inventory.md` | done (POPIA review is human) |

## Phatu items covered this sprint

- **03 Stage 7:** CI commands, recording results, Postgres cases, gameweek replay, load test, the whole controls table, and the launch evidence (architecture test from Sprint 1).
- **01 Gate 8:** targets and the load script.
- **01 Gate 9:** the whole controls table.
- **01 "Matchday operations":** the full section, as `docs/ops/matchday-runbook.md`.
- **01 "Release record":** pre-filled.

**Item I added that Part 2 didn't list:**
- **01 Gate 9:** "the ability to pause scoring or outbound alerts independently". `Operations:ScoringPaused` is tested; the alerts switch is documented for when delivery exists.

## What's left

All of it is human or other-owner work (Part 4):
- live staging runs;
- load test execution;
- timed restore;
- rotation rehearsal;
- independent security review;
- POPIA review.

## Needs sync team

1. **Revoke `sync_worker_user`'s app privileges beyond `SELECT` on `app.gameweeks`:**
   - SELECT on `app.users`, `app.fantasy_teams` and `app.fantasy_squads`;
   - SELECT, INSERT and UPDATE on `app.points_results`, `app.player_prices` and `app.leaderboards`.

   Found by the live permissions check on 2026-11-23.
2. **Rotate `sync_worker_user`'s password**, which is committed in V001.
3. **Run a full-history gitleaks scan on touchline-sync**, and fix its 14 Dependabot alerts (4 critical).

## Needs other owners

1. **Ops:**
   - rotate `app_api_user` (its password is in the sync repo's V001);
   - run the staging load test (`docs/load`);
   - a timed restore rehearsal;
   - a rotation rehearsal;
   - production-role access tests (re-run `effective-permissions.sql` after the revoke);
   - set the server log retention (30 days proposed).
2. **Independent security reviewer:** review the access matrix, configuration and findings; retest.
3. **Privacy officer:** the POPIA review of `docs/security/privacy-inventory.md` (lawful basis, residency, breach procedure, retention).
4. **Release lead:** fill in and sign `docs/release/release-record.md`.
5. **Sportmonks subscription:** still on the free plan. (Carried over.)

## Conflicts

None this sprint.

## Decisions for the team

1. **Admin role:** support and operator share the admin role. A read-only support role (explain any team, no scoring or pricing) is a small follow-up if wanted.
2. **Rate limits:** account changes 10/min, fantasy writes 30/min, standings 60/min per user, anonymous 300/min per IP. They are settings (`RateLimits__*`); tune them after the load test.
3. **Public cache headers:** public football reads are `public, max-age=15`. That's short, so live scores stay current; match freshness is still visible in the body.
4. **Startup validation** applies to Staging and Production only. Development and Testing are unchecked.
5. **The CI secret scan** covers commits since 2026-11-23. A one-off full-history scan is an ops task, because earlier history may hold secrets that need rotating rather than a CI failure.
6. **Audit rows** are written after each admin call, including refused ones. A failure to write one is logged, never turned into a failed request.

## Migration file names

- touchline-api: `docs/migrations/012_admin_audit_log.sql`
- touchline-sync: `db/migrations/V044__admin_audit_log.sql`

Not applied. Safe to rerun. Before 012, nothing is audited and everything else works.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/6-frontend-contract...sprint/7-operate-secure?expand=1 (base: `sprint/6-frontend-contract`)
- touchline-sync: https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/5-migrations...sprint/7-migrations?expand=1 (base: `sprint/5-migrations`)
