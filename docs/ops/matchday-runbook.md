# Matchday runbook

This is adapted from the handbook's "Matchday operations". API routes are in `docs/ops/fantasy-operations.md`.

## Before the first kickoff

- **Season and fixtures:**
  - check the correct PSL season (`GET /api/v1/seasons/current`);
  - check the fixtures (`GET /api/v1/fixtures?date=`);
  - check the published deadline in SAST (`GET /api/v1/gameweeks`, and `deadlineSast` on any squad response);
  - check fixture assignments (match detail `gameweek`), and review postponements (`phase: Postponed`) and moves.
- **Feed:**
  - `GET /api/v1/health/freshness` must be `fresh` with `stalledLiveFixtures: 0`. A healthy server with a stale feed still needs attention;
  - `GET /api/v1/admin/diagnostics` shows the provider quota the worker last saw (`sync.provider_quota`) and any stalled live fixtures;
  - the worker must have recent commits, and there must be one active scheduler (needs sync team).
- **Smoke test:** run a synthetic match read and a staging manager's transfer preview.
- **People:** confirm the on-call primary, the backup and the support escalation route.
- **At the deadline:** run `POST /api/v1/admin/gameweeks/{id}/lock` and check `reconciled: true`. Escalate any `mismatches` before ranks are published.

## During play and settlement

| Signal | Action | Evidence of recovery |
|---|---|---|
| **Stalled live fixture**: `GET /api/v1/admin/diagnostics` has `alerts` (from `sync.live_fixture_health.poll_health = 'stalled'`: a should-be-live fixture not observed by the worker for 3+ minutes), or `/health/freshness` shows `stalledLiveFixtures > 0` | Page the sync owner straight away: the poller is down or rate-limited (check `providerQuota`), or two writers are fighting over the scheduler lease. The API keeps serving the last committed score with `stale: true` | `stalledLiveFixtures` back to 0; match detail `freshness.pollHealth` is `healthy` or `healthy_quiet` |
| `freshness.stale: true` on a live match, or `/health/freshness` not fresh | Check the worker (sync team): upstream response, quota, scheduler. The API keeps serving the last score with stale flags; nothing to do in the API | Match `revision` changes again, `stale: false` |
| A match regresses (wrong score or scorer) | Wait for the worker's correction. The API shows whatever is committed. After final, use the corrections workflow | `revision` changes; corrected points in `explain` |
| Duplicate or misleading pushes | Not applicable until delivery exists (`docs/api/delivery-proposal.md`) | – |
| Deadline write backlog (429s or slow writes) | Watch DB pool and transaction time; clients get clear `409`/`429` codes; never extend a passed deadline | Every accepted action has a receipt (lock report `reconciled`) |
| Final whistles but not enough scoring data | `GET …/settlement`: blocked fixtures list why. Resolve each one after checking it, after the 48 h hold for unattributed events | Settlement `canFinalise: true`; final revision published |
| Correction after publication (`amendmentNeeded: true`) | Run `POST …/corrections/preview`, get approval, then `POST …/corrections` with source and reason | Amendment revision; managers see the correction message |
| A release causes errors | Release runbook: pause scoring or roll back; reconcile | Smoke tests pass; lock report reconciled |

## After each gameweek, record

- scheduled vs actual deadline;
- final fixture time and finalising time;
- corrections and disputes;
- availability and peak load;
- worker quota use (sync team).
