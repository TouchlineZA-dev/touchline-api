# Sprint 1 report: read football facts consistently

**Branches and status**
- **touchline-api:** branch `sprint/1-football-facts`, based on `main` at `fe904ac`.
- **touchline-sync:** branch `sprint/1-migrations`, based on `main` at `15b49d1`.
- **Runbook:** v2, with Part 1b applied in this sprint before the first push.
- **Tests:** 336 passed, 0 skipped, 0 failed. This includes every PostgreSQL-backed test, run locally against a throwaway PostgreSQL 17 server (`TOUCHLINE_TEST_PG`). CI runs the same tests in a Testcontainers container with `TOUCHLINE_REQUIRE_DB_TESTS=true`.

## "Pass when"

| Item | Evidence | Status |
|---|---|---|
| A historical match is served completely from a populated DB | `GET /api/v1/fixtures/{id}`: teams, score, phase, clock and source, kickoff UTC, ordered paged events (45+2 display), lineups XI/bench, substitutions, team stats, coverage, freshness, timestamps, revision, gameweek. Tests in `Football/MatchDetailTests.cs` | done |
| Corrections keep every view consistent | `Corrected_scorer_with_unchanged_score…` (same event id), `Reissued_correction_under_a_new_id…` (soft-rescinded and hard-deleted). List, detail and scoring read the same rows through `MatchStateMatrix`, `MatchClock` and `PlayerIdBridge` | done |
| Explain how to measure worker-commit → API visibility separately from provider delay | "Measuring delay" section below; `freshness.timestamps` in every match detail | done |
| One status/phase/clock implementation shared by list, detail and scoring | `Business/MatchStatus.cs`, `Business/MatchClock.cs`. `MatchFactsService` now takes its end minute from `MatchClock` | done |
| Sportmonks player ids resolved in one place | `Services/PlayerIdBridge.cs` | done |
| Provider-state matrix: all 25 states, unknown codes flagged | `MatchStateMatrixTests` | done |
| Match detail with coverage (lineups/events/stats, unavailable vs zero, unattributed events) | `coverage` block; `Unattributed_goal_and_card…` | done |
| Fixture list filters by date, competition, club | `?date=` (SAST day), `?competitionId=`, `?teamId=`; `Fixture_list_filters…` | done |
| Timestamps: provider, first observation, DB commit, API publication | `freshness.timestamps` (provider timestamp null: not stored) | done |
| 8 regression tests (03 Stage 1 table + handbook) | goal in first half, no lineup, worker outage, corrected scorer, postponed/changed kickoff, same snapshot ×3, half-time reopen, fixture moved | done |
| Calendar: persisted number, explicit assignment with history, backfill, writes return server time | migration 007 / V039; `GameweekService`; admin `PUT /admin/fixtures/{id}/gameweek`, `GET /admin/fixtures/{id}/gameweek-history`; `ServerTimeUtc` on squad and transfer responses; `MigrationTests` | done; applies when V039 is run |
| Standings from football.standings: W/D/L, GF/GA/GD, deductions, tie-break state, official vs derived, awarded without invented events, Netlify filters and tie rules | `GET /api/v1/standings`; `Table_follows_netlify_rules…` | done |
| Architecture test: no Sportmonks credentials or worker URL; handler fails on any host but JWKS | `ArchitectureTests` (outbound guard on every `HttpClient`, config scan, source scan) | done |
| Step 0a: alignment doc | `docs/plans/backend-integration-alignment.md` | done |
| Step 0b.1–2: canonical Flyway fixture, real grants, clean install and populated upgrade | `tests/db/flyway` (pinned, `PIN.md`), `Db/TestDatabase.cs`, `Auth/PostgresFixture.cs`, `Db/MigrationTests.cs` | done |
| Step 0b.3: phathu-dev report | "phathu-dev" section below | done |
| Step 0b.4: codes 11/12/sub direction with regression tests; hand-check against a stored fixture | Codes and tests present. Hand-check of minutes, a sub and captain on fixture 1438 in `docs/audit/FANTASY_AUDIT_2026-11-23.md`. Corrected goal: synthetic tests now; the real GW9 replay pack is Sprint 2's reference week | partial |
| Part 1b: event-code regression (16 = +3, 15 = −2) | `Penalty_goal_16_scores_plus_three_and_own_goal_15_scores_minus_two` | done |
| Part 1b: null/unknown player ids never crash; points to no one; still count for the team | `Null_and_unknown_player_ids…`, `Unattributed_goal_and_card…` | done |
| Part 1b: never read `player_match_stats.player_id` | entity has no `PlayerId`; match detail selects named columns | done |
| Part 1b: unattributed goal/card keeps the fixture under review for a hold window | Counted and flagged in coverage now. The 48 h hold and manual resolution belong to the Sprint 5 gate | partial (Sprint 5) |

## Phatu items covered this sprint

- **03 Stage 1:** all five table rows, calendar, standings, and the pass criteria.
- **03 first tickets #2:** match-detail contract, first half and half-time mapping, freshness, delayed state for missing lineups.
- **02 "Deliver the first end to end flow" §1:** canonical Flyway fixture, real grants, clean install and populated upgrade.
- **02 "Keep the existing API":** phathu-dev review.
- **02 "Make scoring trustworthy":** codes and regression tests.
- **01 Gate 2.2–2.4:** migration tests, persisted calendar, UTC with SAST.
- **01 Gate 3:** state mapping, score/phase/clock/stale exposure, the four timestamps.
- **01 Gate 4:** fixtures by date/competition/club; match detail; standings with deductions and awarded matches; capability flags.
- **01 regression scenarios:** same snapshot ×3, scorer change with score unchanged, rescinded goal (removal part), fixture moved to another week.
- **01 architecture acceptance test:** code side.
- **01 first tickets #4.**

Per-item detail is in `docs/sprints/PHATU-COVERAGE.md`.

**Items I added that Part 2 didn't list:**
- **01 Gate 3:** a partial response must preserve previously known facts. The API never deletes or overwrites football rows, and `DistinctEvents` keeps one row per provider event.
- **01 Gate 2.5:** blank and double weeks are now possible through assignments.

## Measuring delay (worker commit → API visibility, separately from provider delay)

Each match detail response carries `freshness.timestamps`:

| Field | Source | What it measures |
|---|---|---|
| `providerTimestampUtc` | always `null` | Sportmonks' own update time isn't stored by the worker, so provider delay can't be measured from the database. |
| `firstObservedUtc` | earliest `created_at` of the fixture and its events | When the worker first wrote the fixture or event. |
| `dbCommittedUtc` | latest `updated_at` across fixture, events, lineups and stats | When the worker last committed a change. |
| `apiPublishedUtc` | the server clock when the response was built | When the API served it. |

**API visibility delay.** The API reads committed rows on every request: there is no API-side cache on match reads beyond HTTP ETag revalidation. So:
- `apiPublishedUtc − dbCommittedUtc` is the age of the data when served.
- The visibility delay itself is the time between the commit and the first request after it, which is effectively zero.
- To measure it in staging: write an event as the worker would, record the commit time, then poll `GET /fixtures/{id}` every second until `revision` changes. The difference is the end-to-end API visibility delay.

**Provider delay.** This is the time from a real-world event to `dbCommittedUtc`. It can only be estimated against a known kickoff or event minute, because no provider timestamp is stored. It is dominated by the writer's schedule: the hourly Netlify `sync-fixtures`.

**Needs sync team:** store Sportmonks' own update time if the API exposes one, to make provider delay measurable.

## phathu-dev (read only, not merged or changed)

- `git log main..origin/phathu-dev` shows one commit, `f887905 Update allowed origins in development CORS configuration`.
- `git diff main...origin/phathu-dev --stat`: `src/Touchline.Api/appsettings.Development.json`, 1 line.
- It adds `http://localhost:8081` and `http://127.0.0.1:8081` (Expo dev server) to `Cors:AllowedOrigins` for Development.

**Still needed?** Not as a committed change. Origins are per environment: a developer sets `Cors__AllowedOrigins__1=http://localhost:8081` in their `.env`, and staging/production set theirs in hosting (Sprint 6 adds per-environment CORS config).

`main` is 15 commits ahead of `phathu-dev`. The earlier "two commits ahead" auth work and tests in 02 are already on `main`. Recommendation: close the branch without merging.

## What's left (moved to later sprints)

- **Sprint 2:** the reference-week replay (GW9, fixture 1393) as the real-fixture corrected-goal check.
- **Sprint 4:** every write naming its target gameweek.
- **Sprint 5:**
  - completeness gate and unattributed-event hold window;
  - revision history for rescinded events;
  - `gameweek_sync_status` with 0 rows.
- **Sprint 6:**
  - player and club detail;
  - `/player-rankings`;
  - `/matches?fixtureId=` compatibility;
  - OpenAPI examples for the five match states;
  - season rollover and club-move rehearsals.

## Needs sync team

1. Add a Flyway migration for `football.standings`. It exists in production but no migration creates it. Tests mirror it in `tests/db/supplement/001_football_standings.sql`.
2. After V039 is applied, make the completeness writer group fixtures by `app.fixture_gameweek_assignments` (the newest row per fixture) instead of SAST date windows.
3. Exclude state 10 (postponed) from gameweek completeness (default decision 1).
4. On reissued corrections, mark the stale `match_events` row `rescinded = true` (preferred) or delete it. The API handles both (default decision 2).
5. Scheduler (default decision 5): run the Python worker and retire the Netlify football schedules at one recorded cutover, never both at once. The worker has locking, job rows and the `is_current` contract, and is the one designed to write `gameweek_sync_status`.
6. Event and lineup sync stopped on 1 Nov (latest `match_events.updated_at` is 2026-11-01 23:00 UTC; fixtures were last updated 2026-11-18). `sync.sync_jobs` hasn't recorded a run since 8 Aug, because the Netlify writer doesn't log jobs.
7. Update BACKEND_INTEGRATION.md §5 (budget default is 100.0, price default 4.0), §4 (the worker now has a writer for the gate), §6 (all 25 states), and note that the sync report's §1.3 "own goal = 16" is wrong (it's 15).
8. If Sportmonks exposes it, store the provider's own update time, so provider delay can be measured.

## Needs other owners

1. **Sportmonks subscription:** the key is on the free plan, and there has been no PSL data since 18 Nov. The API keeps serving the last known data with stale flags; nothing in the API can fix this. (Owner: whoever holds the Sportmonks account.)
2. **Rotate the passwords** of `sync_worker_user` and `app_api_user`. They are committed in plaintext in touchline-sync `V001__schemas_roles_permissions.sql`. Afterwards, consider removing the passwords from V001 in favour of `CREATE ROLE … LOGIN` without a password, set out of band. (Ops)
3. **Image licence and retention** for player photos hotlinked from `cdn.sportmonks.com`. (Legal/product)

## Conflicts (our rule kept)

| Source | Says | Our rule |
|---|---|---|
| BACKEND_INTEGRATION §6 | Missed penalty (17) is negative | No missed-penalty deduction in our scoring table |
| BACKEND_INTEGRATION §6, §9 | Don't score assists | Assist +2, from the goal's related player |
| BACKEND_INTEGRATION §8 | No minutes; lineup presence is the appearance | Minutes from XI, subs and reds; inclusive counting |
| BACKEND_INTEGRATION §8 | Captain doubles | Captain ×3, vice ×2, Triple Captain ×4 |
| BACKEND_INTEGRATION §5 | Budget 300.0 | R100.0m |

## Decisions for the team

The five defaults below come from runbook Part 1b. They are implemented or planned as described, and Leruo or Phatu can overturn any of them.

1. **Postponed fixtures:** the original gameweek doesn't wait; the fixture's outcome is "postponed".
   - When rescheduled, an admin records a new assignment (`PUT /admin/fixtures/{id}/gameweek`) to the gameweek containing the new kickoff, and points count there (FPL-style blank then double).
   - With no new date, the fixture stays unassigned.
   - V039 doesn't backfill postponed, cancelled or abandoned fixtures.
   - The worker should exclude state 10 from completeness (needs sync team #3).
2. **Reissued corrections:** both worker behaviours are supported, hard-deleting the stale event or marking it rescinded (preferred).
   - The API excludes rescinded or removed events and the revision changes.
   - Flagging the gameweek for rescore arrives with Sprint 5 revisions.
3. **Saves and defensive contributions:** not in our rules; no action.
4. **Unattributed goals and cards:**
   - Sprint 1 counts them for the team and gives the points to no one, and match detail shows `coverage.unattributedEvents`.
   - Sprint 5 will keep the fixture `awaiting_review` for a hold window (setting, default 48 h), after which an admin can finalise through a recorded manual resolution.
5. **Scheduler:** the API doesn't care who writes. The recommendation (Python worker, single recorded cutover) is under needs sync team #5.

Sprint 1 also introduced these new decisions:

6. **`AWAITING_UPDATES` (19):** kept as status `Scheduled` (the 23 Nov audit decision), with phase `AwaitingUpdates` and `needsReview`. Nothing is scored from it.
7. **Suspended (11):** status `NoResult`, but not "settled", so a gameweek with a suspended match can't finalise on fixture states alone.
8. **Awarded (17) and walk-over (14):**
   - status `Finished` with a final result;
   - count in the standings by recorded scoreline;
   - give no player points (no match was played);
   - settle their gameweek.
9. **Interrupted (18):** status `Live`. The clock is paused at the latest event, and provisional points keep counting.
10. **Unknown provider codes:** new status `Unknown` (an additive enum value) with `needsReview: true`. Clients that switch on status need a default branch.
11. **Freshness default:**
    - stale after 75 minutes without a commit, inside a window from 2 h before kickoff to 6 h after;
    - also stale when the window has closed and the fixture still isn't settled;
    - settings live in the `Freshness` section.
12. **Revision id:** `r-` plus the first 16 hex characters of the SHA-256 of the state, scores, every stored event's provider id, type, players, minute and rescinded flag, and the lineup. Timestamps are excluded, so a re-ingest doesn't change it and any correction does.
13. **Standings policy:**
    - derived from results, as the Netlify function does;
    - official points below the derived points with equal games played count as a deduction;
    - any other difference is shown as a discrepancy, and the derived figures are kept.

## Migration file names

- touchline-api: `docs/migrations/007_gameweek_calendar.sql`
- touchline-sync: `db/migrations/V039__gameweek_calendar.sql` (identical SQL plus a one-line header)

Not applied. Safe to rerun. The API works before and after it's applied.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/main...sprint/1-football-facts?expand=1
- touchline-sync: https://github.com/TouchlineZA-dev/touchline-sync/compare/main...sprint/1-migrations?expand=1

(gh isn't installed here, so these are compare links. Open each PR with base `main`.)
