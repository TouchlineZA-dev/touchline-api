# BACKEND_INTEGRATION.md vs touchline-api

Sprint 1, step 0a. Compares each instruction in touchline-sync's `BACKEND_INTEGRATION.md` (sync `main` at `15b49d1`, guide dated 2026-08-08) with what the API does on `sprint/1-football-facts`. It also takes in the runbook's Part 1b facts from the sync owner's report of 23 Nov 2026.

**Status values:**
- **aligned**: the API does what the guide says.
- **fixed**: a plain mismatch, fixed in Sprint 1 with a test.
- **conflict**: the guide disagrees with an agreed Touchline rule. Our rule is kept, and the conflict is listed in the sprint report.
- **stale doc**: the guide no longer matches the database.
- **later**: the work belongs to a later sprint.

## Architecture and access

| # | What the guide says | What the API does | Status |
|---|---|---|---|
| 1 | The integration point is the database, not HTTP. The API and the worker never call each other (§1, §13). | The only outbound call is to Supabase JWKS. `ArchitectureTests` hits every public read through a handler that refuses any other host, and checks the source registers one HTTP client (JWKS) and the config has no Sportmonks or worker URL. | aligned (test) |
| 2 | `football` and `sync` are read-only for the API; `app` is read/write (§1, §11). | `TouchlineDbContext.GuardReadOnlySchemas` refuses writes to `football`/`sync` before they reach the DB. `MigrationTests.Clean_install…` proves `app_api_user` has no INSERT on `football.fixtures` and no UPDATE on `sync.gameweek_sync_status`. | aligned (test) |
| 3 | Connect as `app_api_user` through the transaction pooler on 6543 (§2). | The connection string comes from the environment. `NoResetOnClose = true` is set because the 6543 pooler hangs on `DISCARD ALL`. Since Sprint 1 the database tests also connect as `app_api_user`, with its real grants and RLS. | aligned |
| 4 | No DELETE anywhere; soft-delete with flags (§2). | The API never deletes (leagues use `left_at` and `is_active`; assignments are insert-only). Tests check `app_api_user` has no DELETE on the fantasy and assignment tables. | aligned (test) |
| 5 | Schema changes go through Flyway in the sync repo (§11). | New SQL goes in `docs/migrations/007+` and is mirrored as V043+ on a touchline-sync `sprint/<n>-migrations` branch. It's never applied by the API. | aligned |
| 6 | The guide's §2 prints the pooler host and role name. Sync `V001` commits plaintext passwords for both service roles. | Nothing secret is committed in this repo. The test harness replaces the V001 passwords with its own. | **needs other owners**: rotate both role passwords |

## IDs and joins

| # | What the guide says | What the API does | Status |
|---|---|---|---|
| 7 | Two id spaces: app tables use `football.players.id`; events and lineups use `sportmonks_player_id`. Bridge through `players` and never join across (§7). | `Services/PlayerIdBridge.cs` is now the only place that converts. Scoring (`MatchFactsService`) and match detail (`MatchDetailService`) both use it. | aligned (one place since Sprint 1) |
| 8 | Some event players don't resolve to a players row; fall back to `player_name` (§9). Part 1b: 296 lineup rows, 8 goals, 44 subs and 75 yellows have no player id. | Unresolved players are shown by the provider's name with `resolved: false`. Their events still count for the team score and clean sheets, but no player gets points. Match detail shows `coverage.unattributedEvents`. Tests: `Unattributed_goal_and_card…`, `Null_and_unknown_player_ids…`. | fixed (test) |
| 9 | Fixtures can have null teams or no score (§9). | Null teams show as "TBD". A match that hasn't started has a null score (it used to show the raw value). | aligned / fixed for scheduled matches |

## Codes

| # | What the guide says | What the API does | Status |
|---|---|---|---|
| 10 | Event types 14 goal, 15 own goal, 16 penalty scored, 17 missed penalty, 18 sub, 19 yellow, 20 red, 21 second yellow, 10 VAR (§6). | Same mapping. Part 1b warns that the sync report's §1.3 wrongly says "own goal = 16"; we keep 15. Regression test: `Penalty_goal_16_scores_plus_three_and_own_goal_15_scores_minus_two`. | aligned (test) |
| 11 | Missed penalty (17) is "−" for fantasy (§6). | Our scoring table has no missed-penalty deduction. | **conflict**: our rule kept |
| 12 | Assists aren't reliable; don't build core scoring on them (§6, §9). | Our rule gives +2 for an assist, taken from a goal's related player (Part 1b confirms related = assist). `Fantasy:AssistsEnabled` can turn assists off. | **conflict**: our rule kept |
| 13 | "No minutes on this plan": lineup presence is the appearance proxy (§8, §9). | Our rule works out minutes from the starting XI, substitutions and red cards (inclusive counting, stoppage capped). | **conflict**: our rule kept |
| 14 | Captain doubles (§8). | Captain ×3, vice ×2, Triple Captain ×4 (agreed rule). | **conflict**: our rule kept |
| 15 | States 1, 22, 5, 7, 8, 10, 12, 15, 19 are listed; score only at 5, 7, 8 (§6). | `Business/MatchStatus.cs` has one matrix with all 25 Sportmonks states (Part 1b list plus 26). Final played results are still 5, 7, 8; awarded (17) and walk-over (14) are final but score nobody. Unknown codes come back as `Unknown` with `needsReview`. Fixture list, match detail and scoring all use the matrix. Tests: `MatchStateMatrixTests`. | fixed (test) |
| 16 | Lineup `type_id` = starter or bench, substitution direction (§3). | Starter 11, bench 12, sub `player_id` = on, `related_player_id` = off. Confirmed against 2,489 real subs on 2026-11-22 and matches Part 1b. Regression tests in `SportmonksDecodingTests` and `AuditGapTests`. | aligned (test) |
| 17 | Positions 24 GK, 25 DEF, 26 MID, 27 FWD (§6). | Same. | aligned |

## Gate, timing and data caveats

| # | What the guide says | What the API does | Status |
|---|---|---|---|
| 18 | `sync.gameweek_sync_status.marked_complete` is the "safe to score" gate. It isn't populated yet, so gate on state 5 per fixture (§4, §13). | Finalising is allowed on `marked_complete` OR "every fixture settled and at least one final". Part 1b says the table has 0 rows and finalising must never rely on "all fixtures final" alone. The worker's new writer (sync `4c550f7`) isn't running on production; the Netlify writer is. | **later** (Sprint 5 completeness contract); worker cutover needs sync team |
| 19 | All times are UTC; show them in SAST (§3, §9). | Responses use `…Utc` instants. Gameweek windows and the new `?date=` fixture filter use SAST (UTC+2, no daylight saving). | aligned |
| 20 | `player_match_stats` is team-level and its `player_id` is NULL (§3, §9). Part 1b: the column exists in production from a manual change; never read it. | The entity has no `PlayerId`. Match detail selects named columns only and shows the stats as team stats. Player stats are reported as `unsupported`. | aligned |
| 21 | About 45% of player photos (and some crests) are placeholders; supply a fallback (§12). | `FootballService.ImageOrNull` returns null for any URL containing "placeholder". Clients show their own fallback. Real URLs are passed through as stored (hotlinked from cdn.sportmonks.com). | aligned; image licence and retention need other owners |
| 22 | Data is mutable until final and can be corrected after FT; scoring must be re-runnable (§13). | Finalising upserts `points_results`. Match detail exposes a content-hash `revision` that changes on any correction. Tests cover re-attribution under the same event id and reissue under a new id (soft-rescinded or hard-deleted). | aligned (test) |
| 23 | Eventual consistency at about 1-hour granularity (§13). | The freshness default is 75 minutes inside the match window (`Freshness:StaleAfterMinutes`) and matches the hourly Netlify `sync-fixtures`. | aligned |
| 24 | `football.standings` is handy for display (§3). | The table exists in production but no Flyway migration creates it. `/api/v1/standings` derives the table from results (as the Netlify function does) and uses `football.standings` for deductions and discrepancy checks. Tests use a supplement that mirrors the live columns. | **needs sync team**: add a migration for `football.standings` |
| 25 | `fantasy_teams.budget_remaining` defaults to 300.0; `player_prices.price` defaults to 50.0 (§5). | V035 (applied) sets 100.0 and 4.0. The agreed budget is R100.0m. | **stale doc**: sync team to update §5 |
| 26 | The backend defines which fixtures belong to which gameweek, by date range or round (§8). | Since Sprint 1 the assignment is persisted (`app.fixture_gameweek_assignments`, migration 007 / V043) with history. Until 007 is applied, the API falls back to SAST date windows. The worker's completeness writer still groups fixtures by SAST date window. | aligned; **needs sync team**: read the assignment table once V043 is live |
| 27 | Use `is_current` to find the live season (§4). | Same: `GetCurrentPslSeasonAsync` and the new `GetCurrentSeasonAsync(competitionId)`. | aligned |
