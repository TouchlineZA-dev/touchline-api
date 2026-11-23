# Sprint progress

**Run setup**
- **Mode:** A (stacked PRs, keep going).
- **Runbook:** v2 (23 Nov 2026), applied. Part 1b was folded into Sprint 1 before its first push.
- **Current sprint:** 7 (next to start).

**How to resume:** read `docs/sprints/RUNBOOK.md`, then this file. Then continue from the first sprint that isn't "pushed" or "merged".

| Sprint | Status | touchline-api branch | touchline-sync branch | Migrations | PRs |
|---|---|---|---|---|---|
| 1 Football facts | pushed | `sprint/1-football-facts` | `sprint/1-migrations` | 007 / V039 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/main...sprint/1-football-facts?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/main...sprint/1-migrations?expand=1) |
| 2 Season rules | pushed | `sprint/2-season-rules` (base: sprint 1) | `sprint/2-migrations` (base: sprint 1) | 008 / V040 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/1-football-facts...sprint/2-season-rules?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/1-migrations...sprint/2-migrations?expand=1) |
| 3 Accounts | pushed | `sprint/3-accounts` (base: sprint 2) | `sprint/3-migrations` (base: sprint 2) | 009 / V041 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/2-season-rules...sprint/3-accounts?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/2-migrations...sprint/3-migrations?expand=1) |
| 4 Safe writes | pushed | `sprint/4-safe-writes` (base: sprint/3-accounts) | `sprint/4-migrations` (base: sprint/3-migrations) | 010 / V042 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/3-accounts...sprint/4-safe-writes?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/3-migrations...sprint/4-migrations?expand=1) |
| 5 Settlement | pushed | `sprint/5-settlement` (base: sprint/4-safe-writes) | `sprint/5-migrations` (base: sprint/4-migrations) | 011 / V043 | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/4-safe-writes...sprint/5-settlement?expand=1) · [sync](https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/4-migrations...sprint/5-migrations?expand=1) |
| 6 Frontend contract | pushed | `sprint/6-frontend-contract` (base: sprint/5-settlement) |  | none | [api](https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/5-settlement...sprint/6-frontend-contract?expand=1) |
| 7 Operate and secure | not started | `sprint/7-operate-secure` | | | |

## Migration versions so far

| touchline-api | touchline-sync | Sprint | Applied? |
|---|---|---|---|
| 007_gameweek_calendar.sql | V039__gameweek_calendar.sql | 1 | no |
| 008_rules_versions.sql | V040__rules_versions.sql | 2 | no |
| 009_accounts.sql | V041__accounts.sql | 3 | no |
| 010_safe_writes.sql | V042__safe_writes.sql | 4 | no |
| 011_settlement.sql | V043__settlement.sql | 5 | no |

## Blockers

None.

## Local environment

- Docker isn't installed. The PostgreSQL-backed tests run locally against a throwaway portable PostgreSQL 17 server in the session scratchpad: set `TOUCHLINE_TEST_PG` to its admin connection string.
- Without it, the tests skip locally. CI runs them in Testcontainers and requires them.
- `gh` isn't installed, so PRs are given as compare links.
- touchline-sync work happens in a separate worktree, `../touchline-sync-sprints`, so the main sync checkout is untouched.
