# Sprint 8 report: alignment with the sync owner's V039–V042

**Branches and status**
- **touchline-api:** branch `sprint/8-sync-alignment`, stacked on `sprint/7-operate-secure`.
- **touchline-sync:** our migration branches `sprint/1…7-migrations` were rebuilt on his `main` and renumbered (below).
- **Tests:** see "Pass when".

## What the sync owner shipped (read on 2026-11-23)

| Version | File | Branch | On `main`? |
|---|---|---|---|
| V039 | `V039__version_drift_and_restrict_worker.sql`: versions `football.standings` and `player_match_stats.player_id`; revokes the worker's `app` access except `SELECT` on `gameweeks` | `sprint/3-4-live-freshness-corrections` | **merged** (PR #3, `3d63fd0`) |
| V040 | `V040__live_freshness_and_quota.sql`: `match_events.withdrawn_at`; `sync.fixture_freshness`, `fixture_revisions`, `provider_quota`; `record_fixture_observation()`; views `live_fixture_health` and `fixture_revision_latency` | same | **merged** |
| V041 | `V041__corrections_reschedules_completeness.sql`: `attribution_revision`, `sync.event_changes`, `player_lineups.withdrawn_at`, `fixture_schedule_changes`, `fixture_completeness`, `late_corrections` | same | **merged** |
| V042 | `V042__operations.sql`: `sync_jobs` runner columns, `scheduler_lease`, `ops_responders`, `ops_health`, `ops_alerts` | `sprint/3-4-live-freshness-corrections` (commit `39c3c57`, "Sprint 5") | **not merged** |

**Your expected numbers:** you expected his Sprint 2 to be V039 and his Sprint 3 to be V040, with ours at V041–V046. In fact his Sprints 3–4 landed as V039–V041 in one PR, and V042 is pending. The highest version across all branches is **V042**, so ours start at **V043**.

## 1. Renumbering

| touchline-api | Was | Now |
|---|---|---|
| `007_gameweek_calendar.sql` | V039 | **V043** |
| `008_rules_versions.sql` | V040 | **V044** |
| `009_accounts.sql` | V041 | **V045** |
| `010_safe_writes.sql` | V042 | **V046** |
| `011_settlement.sql` | V043 | **V047** |
| `012_admin_audit_log.sql` | V044 | **V048** |

**How the stack was redone:**
- Each touchline-sync `sprint/n-migrations` branch was rebuilt on his current `main` (V039–V041) with the renamed file, then force-pushed with `--force-with-lease`. Nothing had been opened or merged.
- The headers of `docs/migrations/007–012`, the README mapping (NNN → V(NNN+36)), the code messages, the sprint reports, PROGRESS and FINAL-SUMMARY now use V043–V048.
- These doc updates are in this sprint-8 branch, which merges last. Sprints 1–7 still show the old numbers until it does.

**Test harness:**
- `tests/db/flyway` is now pinned to his `main` (`3d63fd0`) plus his unmerged V042. The full suite passes on all of his migrations followed by ours.
- The `football.standings` test supplement is deleted: V039 creates the table.

### Overlap check: his V039–V042 against our V043–V048

| Object or grant | His | Ours | Result |
|---|---|---|---|
| `football.standings` | V039 creates it (with `UNIQUE (season_id, team_id)`) | only a test-only supplement, never a migration | Supplement deleted. No overlap in migrations |
| Worker's `app` grants | V039 revokes everything except `SELECT ON app.gameweeks`, and revokes default privileges | none of ours grant the worker anything | No overlap. Resolves the Sprint 7 finding |
| `app.gameweeks` | V041's `sync.gameweek_for()` reads it by SAST date window | V043 adds a `number` column and the assignment table | No object overlap; the semantics differ (see needs sync team #3) |
| `match_events` and `player_lineups` columns | `withdrawn_at` (V040, V041), `attribution_revision` (V041) | none | No overlap. The API now filters `withdrawn_at` |
| Completeness | `sync.fixture_completeness` (V041) | `app.gameweek_settlement`, `app.fixture_resolutions` (V047) | Different schemas and objects. The API could read his table as worker evidence (decision 3) |
| Grants to `app_api_user` | SELECT on his new `sync` tables and views; EXECUTE on 3 functions | grants only on `app` tables we create | No overlap |

## 2. API alignment

| Item | Evidence | Status |
|---|---|---|
| Every `match_events` read filters `withdrawn_at IS NULL` (also `player_lineups`, his V041); works before V040 | `Data/FootballReads.cs` is the one read path for events and lineups, used by scoring, match detail, settlement, rankings, player detail, availability and latest clubs. `Withdrawn_events_and_lineup_rows_are_excluded…`; `Before_v040_every_event_counts…` (a V001–V039 database) | done |
| Freshness and revision from `sync.fixture_freshness`; stale for live fixtures from `live_fixture_health.poll_health = 'stalled'`; fallback when absent | `MatchDetailService.SyncFreshnessAsync`:<br>• `revision` is `s{n}` with `revisionSource`;<br>• `freshness.source`, `pollHealth`, `lastObservedUtc`;<br>• `timestamps.providerTimestampUtc` now comes from `last_provider_at`.<br>Tests: `Live_freshness_and_revision_come_from_the_worker…` (healthy_quiet: not stale; stalled: stale) and the pre-V040 fallback test | done |
| Alert on stalled live fixtures in admin diagnostics and the runbook | `GET /api/v1/admin/diagnostics` (alerts, stalled list, quota, freshness); `/health/freshness` adds `stalledLiveFixtures` (a count only) and is not `fresh` while any is stalled; `docs/ops/matchday-runbook.md` has a new first row | done |
| His live-state list wherever the API decides "should be live" | `MatchStateMatrix.ShouldBeLiveStates` = {2, 3, 4, 6, 9, 11, 16, 18, 19, 21, 22, 23, 25, 26}, used by match freshness (window and poll health) and the stalled-fixture fallback. `Should_be_live_matches_the_sync_owners_list_in_v040` parses his view and compares | done |

**Checked against our state matrix:**
- Every state we treat as `Live` (2, 3, 4, 6, 9, 18, 21, 22, 23, 25) is in his list.
- His list adds 11 (suspended), 16 (delayed), 19 (awaiting updates) and 26 (pending). We keep their status and scoring (not live, not scored), but they now count as "should be live" for freshness and stall alerts.
- That's a deliberate split: watching versus scoring. See decision 1.

## "Pass when"

Full suite: **419 passed, 0 skipped, 0 failed**, on the re-pinned schema with V039–V042 and V043–V048. New tests: `Football/SyncAlignmentTests.cs` (4).

## Needs sync team

1. **Deploy order:**
   1. Merge and deploy the API with the `withdrawn_at` filter (this branch, after Sprints 1–7) **before** V040 runs in production. V040 is already on `main`; if it is applied first, withdrawn events would count until the API catches up.
   2. Merge his V042 PR **before** our V043–V048 PRs. Flyway applies in order and refuses an older pending version once a newer one is applied.
2. **Scheduler:** the decision is still open. There must be **only one live writer**. V042's `scheduler_lease` helps, but the Netlify football schedules must be retired at one recorded cutover, or both writers must take the lease.
3. **Gameweek of a kickoff:** `sync.gameweek_for()` and `fixture_schedule_changes` use SAST date windows. Once V043 is applied, they should use `app.fixture_gameweek_assignments` (newest row per fixture), which is the API's authority for which week a fixture counts in. That needs `SELECT` for the worker on that table, a grant his V039 policy would have to allow.
4. **Postponed fixtures:** keep them out of completeness (default decision 1). His `assess_fixture()` should treat state 10 as `postponed`.

## Needs other owners

Unchanged from FINAL-SUMMARY. The Sportmonks subscription is still the urgent one.

## Decisions for the team

1. **"Should be live" is separate from status.** A suspended, delayed or awaiting-updates fixture is watched like a live one (freshness and stall alerts), but it isn't scored and its status doesn't change.
2. **Revision format:** match detail shows the worker's revision (`s{n}`) when `sync.fixture_freshness` has the fixture. Settlement still binds admin resolutions to the API's own content hash, which is computed from the same rows minus withdrawn ones. Both change on the same corrections.
3. **Follow-up (not built):** read his `sync.fixture_completeness` as worker evidence in the settlement contract, alongside `gameweek_sync_status`. It could replace most manual resolutions.

## Migration file names

None new. Renumbered: V043–V048 (table above).

## PR links

See PROGRESS and FINAL-SUMMARY.
