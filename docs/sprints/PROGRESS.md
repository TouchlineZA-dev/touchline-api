# Sprint progress

**Run setup**
- **Mode:** A (stacked PRs, keep going).
- **Runbook:** v2 (23 Nov 2026), applied. Part 1b was folded into Sprint 1 before its first push.
- **Current sprint:** all sprints (1–8) pushed; FINAL-SUMMARY.md written; PRs opened with gh.

**How to resume:** read `docs/sprints/RUNBOOK.md`, then this file. Then continue from the first sprint that isn't "pushed" or "merged".

| Sprint | Status | touchline-api branch | touchline-sync branch | Migrations | PRs |
|---|---|---|---|---|---|
| 1 Football facts | pushed | `sprint/1-football-facts` | `sprint/1-migrations` | 007 / V043 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/main...sprint/1-football-facts?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/main...sprint/1-migrations?expand=1) |
| 2 Season rules | pushed | `sprint/2-season-rules` (base: sprint 1) | `sprint/2-migrations` (base: sprint 1) | 008 / V044 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/1-football-facts...sprint/2-season-rules?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/1-migrations...sprint/2-migrations?expand=1) |
| 3 Accounts | pushed | `sprint/3-accounts` (base: sprint 2) | `sprint/3-migrations` (base: sprint 2) | 009 / V045 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/2-season-rules...sprint/3-accounts?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/2-migrations...sprint/3-migrations?expand=1) |
| 4 Safe writes | pushed | `sprint/4-safe-writes` (base: sprint/3-accounts) | `sprint/4-migrations` (base: sprint/3-migrations) | 010 / V046 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/3-accounts...sprint/4-safe-writes?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/3-migrations...sprint/4-migrations?expand=1) |
| 5 Settlement | pushed | `sprint/5-settlement` (base: sprint/4-safe-writes) | `sprint/5-migrations` (base: sprint/4-migrations) | 011 / V047 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/4-safe-writes...sprint/5-settlement?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/4-migrations...sprint/5-migrations?expand=1) |
| 6 Frontend contract | pushed | `sprint/6-frontend-contract` (base: sprint/5-settlement) |  | none | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/5-settlement...sprint/6-frontend-contract?expand=1) |
| 7 Operate and secure | pushed | `sprint/7-operate-secure` (base: sprint/6-frontend-contract) | `sprint/7-migrations` (base: sprint/5-migrations) | 012 / V048 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/6-frontend-contract...sprint/7-operate-secure?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/5-migrations...sprint/7-migrations?expand=1) |
| 8 Sync alignment | pushed | `sprint/8-sync-alignment` (base: sprint/7-operate-secure) | none (renumbered the sync stack to V043–V048) | none | see PR list below |

## Migration versions so far

The sync owner's V039–V041 (merged to touchline-sync `main`) and V042 (his branch `sprint/3-4-live-freshness-corrections`, unmerged) come first; ours were renumbered to V043–V048 in Sprint 8.

| touchline-api | touchline-sync | Sprint | Applied? |
|---|---|---|---|
| 007_gameweek_calendar.sql | V043__gameweek_calendar.sql | 1 | no |
| 008_rules_versions.sql | V044__rules_versions.sql | 2 | no |
| 009_accounts.sql | V045__accounts.sql | 3 | no |
| 010_safe_writes.sql | V046__safe_writes.sql | 4 | no |
| 011_settlement.sql | V047__settlement.sql | 5 | no |
| 012_admin_audit_log.sql | V048__admin_audit_log.sql | 7 | no |

## Blockers

None.

## Local environment

- Docker isn't installed. The PostgreSQL-backed tests run locally against a throwaway portable PostgreSQL 17 server in the session scratchpad: set `TOUCHLINE_TEST_PG` to its admin connection string.
- Without it, the tests skip locally. CI runs them in Testcontainers and requires them.
- `gh` isn't installed, so PRs are given as compare links.
- touchline-sync work happens in a separate worktree, `../touchline-sync-sprints`, so the main sync checkout is untouched.

## Pull requests (opened with gh, 23 Nov 2026), in merge order

| Order | touchline-api | touchline-sync |
|---|---|---|
| 0 | – | the sync owner's V042 branch `sprint/3-4-live-freshness-corrections` (no PR yet; his V039–V041 merged as #3) |
| 1 | #6 Sprint 1 (base `main`) | #4 V043 (base `main`) |
| 2 | #7 Sprint 2 | #5 V044 |
| 3 | #8 Sprint 3 | #6 V045 |
| 4 | #9 Sprint 4 | #7 V046 |
| 5 | #10 Sprint 5 | #8 V047 |
| 6 | #11 Sprint 6 | – |
| 7 | #12 Sprint 7 | #9 V048 |
| 8 | #13 Sprint 8 (base `sprint/7-operate-secure`) | – |

Each PR's base is the previous sprint's branch: after one merges, retarget the next to `main`. Deploy the API through #13 (the `withdrawn_at` filter) before V040 runs in production.
