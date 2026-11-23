# Phatu coverage

This lists every item in Phatu's three plan docs that the API owns, with the sprint that delivers it and the evidence.

**Docs:**
- **01**: `docs/plans/01-Touchline-Backend-Launch-Handbook.md`
- **02**: `docs/plans/02-platform-api.md`
- **03**: `docs/plans/03-Leruo-API.md`

**Status values:**
- **done**: delivered, with evidence.
- **partial**: what's missing is said in Evidence.
- **not done**: not started.
- **N/A**: excluded by an agreed rule.
- **other owner**: not the API's work; listed in a sprint report.

Items that appear in several docs are listed once and cite every source.

## Stage 1 / Gate 2–4: football facts, calendar, match centre

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 1 | 03, 01 | Stage 1; Runtime ownership | One ID domain; Sportmonks player ids resolved to internal ids in one place | 1 | `Services/PlayerIdBridge.cs`, used by `MatchFactsService` and `MatchDetailService` | done |
| 2 | 03, 01 | Stage 1; Gate 3; ticket 4 | One implementation of match status and clock for scorecard, timeline and scoring | 1 | `Business/MatchStatus.cs` (`MatchStateMatrix`), `Business/MatchClock.cs`; `MatchStateMatrixTests` | done |
| 3 | 03, 01 | First tickets #2; Gate 3; ticket 4 | First half and half-time no longer fall through to scheduled; every provider state mapped; unknown codes preserved and flagged | 1 | `Every_provider_state_is_mapped_explicitly` (25 states), `Unknown_codes_are_preserved_and_flagged…` | done |
| 4 | 03 | Stage 1 table, row 1 | First-half goal: live phase, confirmed score, kickoff, timing source, update time; same goal in list, detail and scoring | 1 | `First_half_goal_is_live_with_the_same_goal_in_list_detail_and_scoring` | done |
| 5 | 03 | Stage 1 table, row 2 | No confirmed lineup: coverage status plus last known info; fixture opens before lineups | 1 | `Fixture_without_a_lineup_still_opens_with_lineups_pending_and_no_score` | done |
| 6 | 03, 01 | Stage 1 table, row 3; Architecture acceptance test | Worker outage: confirmed score, last observation, stale status; zero outbound football requests | 1 | `Worker_outage_reads_succeed_marked_stale_with_zero_outbound_requests`; `ArchitectureTests` | done (code side); staging run with the worker stopped is the joint sign-off |
| 7 | 03, 01 | Stage 1 table, row 4; regressions | Corrected scorer, unchanged score: timeline updated, identifiable revision, points move exactly once | 1 | `Corrected_scorer_with_unchanged_score…`, `Reissued_correction_under_a_new_id_never_double_counts` | done |
| 8 | 03, 01 | Stage 1 table, row 5; regressions | Postponement or changed kickoff: football state correct; fantasy week resolved separately; a locked entry can't be rewritten without a recorded decision | 1 | `Postponed_fixture_shows_its_state…`, `Fixture_moved_to_another_week…locked_weeks_need_a_decision` | done |
| 9 | 03, 01 | Stage 1; Gate 2.3 | Persist stable gameweek numbers and intentional fixture assignments with history | 1 | migration 007 / V039; `GameweekService`; `Populated_upgrade_backfills…` | done (applies when 007 is run) |
| 10 | 03, 01 | Stage 1; Gate 2.4 | Writes include server time, target gameweek id and deadline | 1 | `SquadView.ServerTimeUtc`, `TransferResult.ServerTimeUtc`; gameweek id and deadline already in `gameweek` | partial: every write naming its target week is Sprint 4 |
| 11 | 03, 01 | Stage 1; Gate 4 standings | Official or reconciled PSL standings, with a policy for deductions and awarded results | 1 | `GET /api/v1/standings`, `StandingsService`; `Table_follows_netlify_rules…` | done |
| 12 | 03 | Stage 1 pass | A historical match served completely from a populated DB | 1 | `GET /api/v1/fixtures/{id}` (events, lineups, subs, stats, coverage, freshness, revision) | done |
| 13 | 03, 01 | Stage 1 pass; Gate 3 | Measure worker-commit → API visibility separately from provider delay | 1 | `freshness.timestamps` (firstObserved, dbCommitted, apiPublished; provider timestamp null); method in sprint-1-report | done |
| 14 | 01 | Gate 4: fixtures and match detail | By date, competition and club; phase, score, clock, events, lineups, subs, team stats; stable ids; ordered, paged events | 1 | `/fixtures?date=&competitionId=&teamId=`, `/fixtures/{id}?eventsPage=` | done |
| 15 | 01 | Gate 4 acceptance | A phone reopening at half-time sees the same score and timeline; a scheduled match has an unknown score | 1 | `Phone_reopening_at_half_time…`; null scores before kickoff | done |
| 16 | 01 | Gate 4 richer coverage | Capability flags per fixture: unavailable vs actual zero | 1 | `coverage` block (lineups, events, team stats, player stats unsupported, unattributed events) | done |
| 17 | 01 | Gate 3 | Expose score, phase, clock source, last update and stale status | 1 | match detail `clock`, `freshness` | done |
| 18 | 01 | Regressions | Same snapshot ingested three times: one event set, points once | 1 | `Same_snapshot_ingested_three_times…`; `MatchFactsDecoder.DistinctEvents` | done |
| 19 | 01 | Regressions | A fixture moved into another week: points follow the approved assignment; history records it | 1 | `Fixture_moved_to_another_week…` | done |
| 20 | 01 | Regressions | A red card or goal rescinded: contribution removed; history has original and correcting revisions | 1, 5 | Removal: `Reissued_correction…` (Sprint 1). Revision history: Sprint 5 | partial |
| 21 | 01 | Gate 2.2 | Migrations tested on empty and on a populated previous release, with the restricted roles | 1 | `MigrationTests` (clean install, populated upgrade, rerun), API as `app_api_user` | done |
| 22 | 01 | Gate 2.5 | Rehearse blank/double weeks, promoted clubs, club moves, season rollover | 1, 6 | Double/blank via assignments (Sprint 1); club moves and rollover tests in Sprint 6 | partial |
| 23 | 01 | Gate 4: player detail | Stable identity, club, position, eligibility, minutes, upcoming fixtures, points history; duplicate names and club moves by id | 6 | | not done |
| 24 | 01 | Gate 4: news | Category id/slug mapping, team tags, attribution, paging | 6 | | other owner (news service); API contract proposal in Sprint 6 |
| 25 | 01 | Gate 4: follows and alerts | Opt-in by device, dedup, corrections | 6 | `docs/api/delivery-proposal.md` | not done |

## Stage 0 (02): platform foundation

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 26 | 02 | First flow 1 | Test fixture consumes the canonical Flyway migrations, not a deleted V033 file | 1 | `tests/db/flyway` (pinned to sync `15b49d1`), `Db/TestDatabase.cs`, `Auth/PostgresFixture.cs` | done |
| 27 | 02 | First flow 1 | Auth and fantasy persistence tests on disposable Postgres with the real grants; clean install and populated upgrade | 1 | API connects as `app_api_user` in every DB test; `MigrationTests` | done |
| 28 | 02 | Keep the API | Review the `phathu-dev` work and its local `appsettings.Development.json` change | 1 | sprint-1-report "phathu-dev" | done (report) |
| 29 | 02 | Scoring trustworthy | Starter, bench and substitution-direction codes confirmed with regression tests | 1 | `SportmonksCodes.cs` (11/12/on = player_id); `SportmonksDecodingTests`, `AuditGapTests` | done |
| 30 | 02 | Scoring trustworthy | Hand-check minutes, a sub, captain points and a corrected goal against a stored PSL fixture | 1, 2 | `docs/audit/FANTASY_AUDIT_2026-11-23.md` (fixture 1438); corrected goal on real fixture 1393: `Reference/ReplayTests.cs` | done |
| 31 | 02 | First flow 2 | Same Supabase project as both clients; verify issuer, audience, signature, subject | 3, 7 | Issuer/audience/signature/expiry pinned per environment (`TokenValidationTests`); same-project setup in `docs/accounts/provider-checklist.md` | partial (code done; dashboard setup is other owner; key-rotation test Sprint 7) |
| 32 | 02 | First flow 2 | Keep the 409 recovery for legacy and conflicting emails | 3 | `UserConflictException` codes kept; `Email_held_by_…` tests; recovery in `docs/accounts/policies.md` | done |
| 33 | 02 | First flow 2 | Profile update (display name/username, favourite club), team rename, stable club ids and a web-club mapping | 3 | `PATCH /me {username, favouriteClubId}`, `PATCH /fantasy/team`, `GET /clubs`, `docs/accounts/club-mapping.md` | done |
| 34 | 02, 03 | First flow 3; Stage 6 | Shared-team end-to-end: admin gameweek → /me → team → 15 picks → snapshot on a second client; stale edit gets 409 | 6 | | not done |
| 35 | 02 | Public contract | `kickoffUtc` vs web `kickoff`; retire synthetic ratings | 6 | | not done |
| 36 | 02 | Public contract | Port `/standings`, `/player-rankings`, `/matches?fixtureId=` with their filters and tie rules | 1, 6 | `/standings` (Sprint 1), match detail (Sprint 1); `/player-rankings` and the `/matches` alias in Sprint 6 | partial |
| 37 | 02 | Public contract | Headlines and category contract through C#, `/news/headlines?max=` compatibility | 6 | | not done |
| 38 | 02 | Public contract | Request/response examples; keep 400/401/403/404/409/503; expose ETag to browser origins; atomic version checks; two-client test | 4, 6 | | not done |
| 39 | 02 | Public contract | Export OpenAPI from CI; contract tests on real envelopes | 6 | | not done |
| 40 | 02 | Public contract | Local launch target and staging/production config: scoped role, Supabase settings, explicit origins | 6 | | not done |
| 41 | 02 | Public contract | Monitor DB readiness and data freshness separately | 6 | | not done |
| 42 | 02 | Scoring trustworthy | Finaliser uses `sync.gameweek_sync_status`; completeness for late facts and postponements | 5 | | not done |
| 43 | 02 | Scoring trustworthy | Owners or schedules for creating gameweeks, finalising/rescoring and applying window prices | 5 | `docs/ops/fantasy-operations.md` | not done |
| 44 | 02 | Scoring trustworthy | Postgres tests: repeated scoring, duplicate transfers, league capacity, two-device edits | 4, 5, 6 | | not done |
| 45 | 02 | Release gate | A non-admin can't score; final score and rank match a hand calculation and stay stable after a rerun | 5 | | not done |

## Stage 2 / Gate 1: season rules

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 46 | 03, 01 | First tickets #1; Gate 1.4 | Versioned rulebook: captaincy, subs, chips, transfers, sale prices, corrections, ties, late entries, reassignments, rounding | 2 | `docs/rules/rulebook-2026-27-v1.md`, `GET /api/v1/rules` | done (sign-off is human) |
| 47 | 03, 01 | Stage 2 table; FPL parity | Each FPL Classic difference listed for product sign-off (12 capabilities) | 2 | rulebook §15; `/rules` `differencesFromFplClassic` | done (sign-off is human) |
| 48 | 01 | Gate 1.2–1.3 | Coverage matrix: confirmed zero, missing, delayed, unsupported per field; what blocks settlement; what the API shows | 2 | `docs/rules/coverage-matrix.md` | done |
| 49 | 03, 01 | Stage 2 pass; Gate 1 pass | Reference squads independently calculated; a second reviewer reproduces them | 2 | `docs/rules/reference-week.md` (65, 61); `ReferenceWeekTests`; `tools/score_gw9.py` | done (second human reviewer pending) |
| 50 | 03 | Stage 2 pass | Keep the rules version on historical entries | 2 | migration 008 / V040; `Finalising_stamps_the_rules_version…` | done |
| 51 | 03, 01 | Stage 2 deadline row | Publish the exact deadline; protect published history; when a future deadline may change | 2 | `deadlineSast`; `PUT /admin/gameweeks/{id}/deadline`; rulebook §3 | done |
| 52 | 01 | Gate 1.4 | Machine-readable rulebook for mobile | 2 | `GET /api/v1/rules` | done |
| 53 | 03 | Stage 2 scoring row | Version the points table | 2 | `2026-27.points.v1`, stored per entry and points row | done |
| 54 | 01 | Reference calculation | 42/34 FPL-rule example vs our 44/41 | 2 | sprint-2-report "Conflicts" | conflict (logged; sign-off pending) |

## Stage 3 / Gate 5: accounts

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 55 | 03, 01 | Stage 3.2; Gate 5.2 | Verified subject is the permanent key; linking flow; recovery for unlinked accounts, email conflicts, missing email | 3 | `UserService` (sub key, optional email); linking test; `policies.md` § Recovery | done |
| 56 | 03, 01 | Stage 3.3; Gate 5.3 | Username: length, characters, NFKC, case-fold, reserved, unique canonical key, stable conflict code, visible policies | 3 | `UsernameRules`, `AccountService.UpdateAsync`, migration 009; `AccountTests` | done |
| 57 | 03, 01 | Stage 3.4; Gate 5.4 | Team rename by owner; permanent id; name history; picks, memberships and ranks kept; names may repeat | 3 | `RenameTeamAsync`, `fantasy_team_name_history`; rename test | done |
| 58 | 03, 01 | Stage 3.5; Gate 5.5 | Onboarding state, favourite club, notification preferences, export, deletion, recent auth, leaderboard privacy | 3 | `GET /me` onboarding, favourite club, `/me/notifications`, `/me/export`, `DELETE /me`, recent auth; leaderboards name-only | done |
| 59 | 03, 01 | Stage 3 pass | Tests: Apple relay, missing Facebook email, repeated callbacks, expired sessions, conflicting names, deleted and suspended accounts | 3 | `AccountTests` + auth tests (relay, no email, repeated calls, expired, conflicts, deleted, suspended) | done (code side; staging run is human) |
| 60 | 03, 01 | Stage 3.1; Gate 5.1 | Provider credentials, callbacks, Apple secret rotation, Facebook non-developer user | 3 | `docs/accounts/provider-checklist.md` | other owner (checklist delivered) |

## Stage 4 / Gate 6: deadline-safe writes

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 61 | 03, 01 | Stage 4 table | Initial pick validation and autopick preview | 4 | | not done |
| 62 | 03, 01 | Stage 4 table | Pre-deadline writes name the week; expired week → conflict with current state | 4 | | not done |
| 63 | 03, 01 | Stage 4 table | Transfer preview with versions | 4 | | not done |
| 64 | 03, 01 | Stage 4 table | Transfer confirm at one decision point; idempotency key bound to manager, operation and payload | 4 | | not done |
| 65 | 03, 01 | Stage 4 table | Chips: entitlements, one per week, activation/cancellation, effect on hits; Free Hit restoration | 4 | | partial: Free Hit is N/A under our rules |
| 66 | 03, 01 | Stage 4 table | Immutable deadline entry; carry-forward; rivals' picks hidden | 4 | | not done |
| 67 | 03, 01 | Stage 4 | DB-backed concurrent-write check, including concurrent first saves | 4 | | not done |
| 68 | 01 | Matchday ops | Reconcile accepted receipts with locked entries at the deadline | 4 | | not done |
| 69 | 03, 01 | Stage 6 | Error semantics with machine-readable codes | 4 | | not done |

## Stage 5 / Gate 7: settlement

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 70 | 03, 01 | Stage 5.1 | State machine open → locked → live → awaiting_review → final, with reason, revision and time | 5 | | not done |
| 71 | 03, 01 | Stage 5.2; ticket 5 | Completeness contract: verified zero, delayed, awarded, abandoned, manual resolution | 5 | | not done |
| 72 | 03, 01 | Stage 5.3 | Per-fixture scoring, on-pitch intervals, preserved inputs, captain fallback after all fixtures, reasons recorded | 5 | | partial: per-fixture and intervals exist; reasons recorded in Sprint 5 |
| 73 | 03, 01 | Stage 5.4 | Provisional, final and amendment revisions; atomic batches; resume without double counting | 5 | | not done |
| 74 | 03, 01 | Stage 5.5 | Ranks from the same revision; ties, late joins, eligibility; cached, paged, efficient "my rank"; included in load tests | 5, 7 | | not done |
| 75 | 03, 01 | Stage 5.6 | Support explanation view; audited correction workflow with preview and correction message | 5 | | not done |
| 76 | 01 | Matchday ops | Week stays under review when whistles have gone but scoring data is short | 5 | | not done |

## Stage 6: frontend contract

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 77 | 03, 01 | Stage 6; API handoff | Versioned OpenAPI with routes, bodies, errors, auth and examples | 6 | | not done |
| 78 | 03, 01 | Stage 6 | Staging account, stable fixture ids, two reference managers, resettable private league | 6 | | not done |
| 79 | 03, 01 | Stage 6 table | `PATCH /me`, `PATCH /fantasy/team`, autopick, preview, receipts, chip entitlements, rules, finality, explanations | 3–6 | `PATCH /me`, `PATCH /fantasy/team` (Sprint 3); rules (Sprint 2); rest Sprints 4–6 | partial |
| 80 | 03, 01 | Stage 6 table | Leagues: pagination, rank history, visibility, ownership transfer/deletion policy; tied ranks, concurrent joins, repeated joins, owner departure, public rank vs unpublished squad | 6 | | not done |
| 81 | 03, 01 | Stage 6 table | Club and player detail, coverage flags, freshness, paging | 1, 6 | coverage and freshness (Sprint 1) | partial |
| 82 | 03, 01 | Stage 6 table | Delivery: devices, follows, preferences, last synced state | 6 | proposal doc | not done |
| 83 | 03 | Stage 6 | Shared standings cached by revision and page; manager responses scoped to the account | 5, 6 | | not done |
| 84 | 03 | Stage 6 pass | Frontend developer completes the flow on staging APIs with no DB edits | 6 | | not done |

## Stage 7 / Gates 8–9: operate and secure

| # | Doc | Section | Item | Sprint | Evidence | Status |
|---|---|---|---|---|---|---|
| 85 | 03, 01 | Stage 7 | CI runs the full suite with DB tests required; record the result against the revision | 7 | CI already sets `TOUCHLINE_REQUIRE_DB_TESTS` | partial |
| 86 | 03, 01 | Stage 7 | Postgres cases: concurrent mutations, deadline freeze, settlement interruption, duplicate retries; full gameweek replay | 4, 5, 7 | | not done |
| 87 | 03, 01 | Stage 7; Gate 8 | Load test: standings, deadline writes, cache misses, goal updates; handbook targets | 7 | | not done |
| 88 | 03, 01 | Gate 9 identity | Pinned issuer/audience, signatures, expiry; key rotation, expiry, suspension tests | 7 | partly exists (`TokenValidationTests`) | partial |
| 89 | 03, 01 | Gate 9 ownership | Every private read/write per role; ownership checked in ASP.NET because RLS is broad; effective permissions of direct Supabase roles | 7 | | not done |
| 90 | 03, 01 | Gate 9 secrets | Production startup rejects incomplete config; separate envs; dependency checks; secret scanning; rotation rehearsal | 7 | | not done |
| 91 | 03, 01 | Gate 9 data | Public reads cached separately; squads, invite codes, emails and device tokens never leak; minimal public health | 6, 7 | | not done |
| 92 | 03, 01 | Gate 9 abuse | Rate and size limits; privileged ops restricted; audit log (actor, time, reason, outcome) | 7 | | not done |
| 93 | 03, 01 | Gate 9 privacy | Privacy inventory, retention, backups vs deletion, POPIA review | 7 | | not done |
| 94 | 03, 01 | Gate 9 release | Restore, rollback, rotation, escalation, pause scoring/alerts independently | 7 | `docs/ops/release-runbook.md` | not done |
| 95 | 01 | Matchday operations | Before first kickoff, during play and settlement, signal → action → evidence | 7 | `docs/ops/matchday-runbook.md` | not done |
| 96 | 01 | Release record | Gates 1–9 table and checklist | 7 | `docs/release/release-record.md` | not done |
| 97 | 03 | Launch evidence | Contract examples for scheduled, live, stale, postponed and final matches | 1, 6 | Sprint 1 tests produce all five; examples go in OpenAPI in Sprint 6 | partial |

## Totals

| Status | Count |
|---|---|
| done | 39 |
| partial | 12 |
| not done | 43 |
| other owner / N/A / conflict | 3 |
| **Total** | **97** |
