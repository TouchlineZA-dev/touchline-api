# Budget fix: R100.0m, one setting (2026-11-25)

Testers saw a R300m budget. The agreed rule is **R100.0m**.

## Where 300 came from

`main` already used 100.0 on every code path. On 2026-11-25, live still created two teams with R300m (see below). The database default is 100.0, and the API always sets the budget itself on a new team. So the API running then had `Fantasy:Budget = 300.0`. There are two ways that can happen:

1. **The deploy was built from `phathu-dev`.** That branch has `Budget = 300.0m` in `Fantasy/FantasyRules.cs`, `"Budget": 300.0` in `appsettings.json`, and R300m in `Fantasy/README.md` and `docs/proposed-migrations/V034__fantasy_core.sql`. The sync handoff (touchline-sync `docs/sprint6/api-handoff.md` §13) already says not to merge it.
2. **The host sets `Fantasy__Budget=300`.** Environment variables beat appsettings and `.env`.

Check both on Railway: which branch or commit it deploys, and whether `Fantasy__Budget` exists. After this PR, the first log lines say `Fantasy budget: <n>`, which shows the effective value directly.

### Every 300 found (all branches, both repos)

| Where | What it said | Action |
|---|---|---|
| api `phathu-dev`: `FantasyRules.cs`, `appsettings.json`, `Fantasy/README.md`, `docs/proposed-migrations/V034` | Budget 300.0 / R300m | Not merged. Don't deploy this branch. |
| api `Khanya`: `README.md:156`, `FootballService.cs:16` | "a R300.0m budget" (comments only; code is 100.0) | Left alone (not on main); fix when Khanya merges. |
| api `funi/userlogic`: `Fantasy/README.md:10` | "the app schema default is 300.0" | Stale note, not on main. |
| api main `FantasyRules.cs:142` | "(A 300.0 budget would use 3.0.)" | Reworded to "scale = budget ÷ 100". |
| api main `Fantasy/README.md:104` | "300.0 → 3.0" | Reworded. |
| api main `docs/plans/02-platform-api.md:16` | "Use the configured R300m starting budget" | Now R100.0m. |
| api main `tests/db/flyway/V025__fantasy_teams_budget.sql` | `DEFAULT 300.0` | Pinned copy of an applied migration; never edit. V035 sets 100.0. |
| api main `docs/sprints/*`, `docs/plans/02-platform-api-status.md`, `backend-integration-alignment.md` | "R300m scrapped" (historical notes) | Left as history. |
| sync `db/migrations/V025__fantasy_teams_budget.sql` | `DEFAULT 300.0` | Applied migration; superseded by V035 (`SET DEFAULT 100.0`). Live default is **100.0**. No new migration needed. |
| sync `db/supabase_schema.sql:299` | `DEFAULT 300.0` | Generated V025 snapshot, marked "DO NOT EDIT"; left as is. |
| sync `BACKEND_INTEGRATION.md:213` | "R100m vs R300m still awaiting sign-off" | Updated: R100.0m agreed (sync PR). |
| sync `docs/sprint6/api-handoff.md:88,101` | "pending sign-off"; "`phathu-dev` has R300m" | Historical report; left. |

Not budget-related: `AnonymousPerMinute = 300`, a 300 s cache, quota "300 calls" in V042/V049, and 300 in test data.

## What changed (this PR)

- **One setting:** `FantasyRules.Budget`, default `100.0m`. Removed from `appsettings.json`, because a second copy is where `phathu-dev` drifted. The `FantasyTeam.BudgetRemaining = 100.0m` entity default is gone. The property is now `required`, so every new team must take its budget from the rules. Team creation, staging kit, squad validation, transfers, autopick, `GET /api/v1/rules` and the snapshot all read that one value.
- **Startup log:** `Fantasy budget: 100.0`, logged once.
- **Env var documented:** `Fantasy__Budget` in `README.md` and `docs/api/deployment.md`, with the instruction that it should **not be set** on any host.
- **Tests** (`Fantasy/BudgetTests.cs`, real pipeline and Postgres):
  - a new team gets exactly 100.0;
  - a 100.0 squad is allowed (bank 0.0), and a 100.1 squad gets 400 with "over the R100.0m budget" and the bank unchanged;
  - `/rules` returns `squad.budget` 100.0;
  - snapshot bank = 100.0 − squad cost;
  - one setting moves creation, validation, `/rules` and the snapshot together;
  - the startup log line appears once.

  With the default changed to 300.0, 7 of the 8 fail.

## Existing data (read-only check, 2026-11-25, as `app_api_user`)

Teams whose bank plus the latest squad's purchase cost is over 100.0:

| Team id | Name | Created | Bank | Squad cost | Total |
|---|---|---|---|---|---|
| 3 | KhanyaFC | 2026-11-25 | 236.0 | 64.0 (15 players) | 300.0 |
| 4 | lwdmkwe | 2026-11-25 | 300.0 | none | 300.0 |

Neither team has made transfers. Both are in a league. Team 3 has one gameweek entry. Teams 1 (Test XI) and 2 (Audit FC) are correct at 100.0. Nothing was changed.

## Decisions for Leruo

1. **Railway:** confirm which branch or commit it deploys (it must be `main`, not `phathu-dev`). Confirm `Fantasy__Budget` is not set, and delete it if it is. After the deploy, the log should say `Fantasy budget: 100.0`. The fix was not applied to Railway from here.
2. **Reset the two test teams** with `docs/migrations/maintenance/reset_r300m_test_teams.sql`:
   - It uses UPDATE only, on `app.fantasy_teams.budget_remaining`, with no DELETE.
   - It sets bank = 100.0 − squad cost, so team 3 → 36.0 and team 4 → 100.0. Squads, entries and league memberships stay.
   - It refuses unless the affected teams are exactly {3, 4}.
   - It refuses and lists any squad over R100.0m, because clearing one needs a DELETE. There are none today.
   - It ends in `ROLLBACK`, so the first run is a dry run. Switch it to `COMMIT` after checking the output.

   It was tested on a throwaway database with the same rows: the dry run changed nothing, COMMIT reset 3 and 4, and a rerun refused. **Do step 1 first**, or new R300m teams keep appearing.
3. **Or simply ignore them:** they are pre-launch test teams. Only reset them if testers keep using them.
4. **`phathu-dev`:** close it, or rebase it onto main, so it can't be deployed again by mistake.
