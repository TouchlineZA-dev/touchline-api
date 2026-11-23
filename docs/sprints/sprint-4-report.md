# Sprint 4 report: deadline-safe writes

**Branches and status**
- **touchline-api:** branch `sprint/4-safe-writes`, stacked on `sprint/3-accounts`.
- **touchline-sync:** branch `sprint/4-migrations`, stacked on `sprint/3-migrations`.
- **Tests:** 383 passed, 0 skipped, 0 failed, including the PostgreSQL tests in `SafeWrites/SafeWriteTests.cs` (14 tests). Those ran three times in a row with no flakes after the team-row lock went in.

## "Pass when"

| Item | Evidence | Status |
|---|---|---|
| The squad is always legal | Every write path still validates the whole squad (`SquadRules`); confirmation re-validates at the decision point; invalid quotes can't be confirmed | done |
| Each accepted action has one receipt | `app.fantasy_receipts` (migration 010); `A_response_lost_after_commit_is_recovered_by_retrying_with_the_same_key`, `A_deadline_during_an_api_restart_keeps_the_persisted_receipt_recoverable` | done for transfer confirmations; other writes return their result plus version (decision 3) |
| The bank and hits balance | The confirmation writes transfers, picks, bank, allowance, version and receipt in one transaction; `Duplicate_transfers_from_two_clients_charge_once`; the lock report reconciles receipts and transfer rows against locked entries | done |
| Target gameweek on every squad/lineup/transfer/chip write; responses return server time, target gameweek, deadline and version; expired week → 409 with current state; never edits the next week | `WritableGameweek(…, gameweekId, requireTarget)`; `Writes_must_name_the_target_gameweek_and_a_locked_week_is_refused_with_the_current_state`; `A_quote_that_straddles_the_deadline_is_refused_and_nothing_is_saved` | done |
| Atomic version check in the same transaction; concurrent first saves | `version` is an EF concurrency token (`UPDATE … WHERE version = @read`), and writes lock the team row (`SELECT … FOR UPDATE`) first; `Two_devices_editing_one_version_one_wins…`, `Concurrent_first_saves_keep_one_squad…` | done |
| Initial pick validation | Existing `SquadRules.ValidateSquad`: eligibility, price/budget, 15 players, club cap, positions, starters/formation, bench order, captaincy. Now checked inside the team lock | done |
| Autopick preview (valid candidate or explanation, no save) | `POST /fantasy/team/autopick/preview`; `Autopick_preview_returns_a_legal_squad_or_an_explanation_and_saves_nothing` | done |
| Transfer preview (Phatu's quote shape) | `POST /fantasy/team/transfers/preview`; `Preview_quotes_costs_bank_free_transfers_hit_and_versions_without_saving` | done |
| Transfer confirm: quote versions + target gameweek + Idempotency-Key; one decision point; key bound to manager + operation + payload hash; price change → 409 new quote; receipt | `POST /fantasy/team/transfers/confirm`; tests for lost response, key reuse, missing key, price change, expiry, insufficient funds | done |
| Error semantics with machine-readable codes | `docs/api/errors.md`; `code` on every fantasy/account error | done |
| Transfer allowance ledger (granted/used per gameweek, no rollover) | `GET /fantasy/team/transfers/ledger`; `free_transfers_granted` stored on confirm; ledger test | done |
| Chips: entitlements GET, one per week, activation/cancellation policy, effect on charged hits; no Free Hit | `GET /fantasy/team/chips`, `DELETE /fantasy/team/chips/{chip}`; `Chips_show_entitlements…`; Wildcard waives the week's charged hits (`transfer_cost` 0) | done |
| Lock and carry-forward: immutable entry at the deadline; rivals' picks hidden; operator reconciliation report | `SquadStore.LockAsync` (snapshot written once); `POST /admin/gameweeks/{id}/lock`; finalising locks first; lock test | done (decision 4 on how the lock is triggered) |
| Postgres tests: two devices, lost response, price change, straddle, duplicate transfers, key + different payload, deadline during restart | All 7 in `SafeWriteTests` | done |

## Phatu items covered this sprint

- **03 Stage 4:** the whole table, the DB-backed concurrent-write check, and the pass criteria.
- **01 Gate 6:** same as 03 Stage 4.
- **01 "Regression scenarios":**
  - two clients submit the same transfer;
  - identical retry key with a different transfer;
  - repricing between preview and confirm;
  - a deadline coinciding with an API restart.
- **01 "Matchday operations":** at the deadline, reconcile accepted receipts with locked entries.
- **03/01 Stage 6:**
  - the transfer quote example;
  - confirmation semantics (expiry, changed price, insufficient funds, stale squad, locked gameweek, unauthorised);
  - machine-readable codes while keeping the working statuses;
  - autopick preview, explicit target week, version and retry semantics;
  - transfer preview and receipts;
  - chip entitlements.
- **02 "Complete the public contract":** atomic version checks on concurrent lineup and transfer writes, tested with two clients editing the same team.

**Items I added that Part 2 didn't list:**
- **01 Gate 6:** "Restrict rivals' unpublished decisions." No endpoint returns another manager's squad; a test for "public rank vs unpublished squad" is in Sprint 6.
- **01 Gate 8:** "Require durable acceptance before acknowledging a squad write." Every write commits before it responds, and confirmations are idempotent.

## What's left

- **Sprint 5:** settlement revisions use the locked snapshot.
- **Sprint 6:** OpenAPI examples for every new endpoint and error; the "public rank vs unpublished squad" test.
- **Sprint 7:** load test of deadline writes (p95 < 1 s, zero lost or duplicate writes, ledger reconciliation).

## Needs sync team

Nothing new. Earlier lists stand.

## Needs other owners

1. **Web and mobile:**
   - send `gameweekId` on every squad, lineup, transfer and chip write (`target_gameweek_required` otherwise);
   - send an `Idempotency-Key` (a UUID per confirmation, reused on retry) with `POST /transfers/confirm`;
   - handle the codes in `docs/api/errors.md`.
   - `Fantasy:RequireTargetGameweek=false` keeps the old behaviour during the client rollout if needed.
2. **Ops:** run `POST /api/v1/admin/gameweeks/{id}/lock` just after each deadline, or schedule it (see decision 4), and check `reconciled`.
3. **Sportmonks subscription:** still on the free plan. (Carried over.)

## Conflicts

**Free Hit:** Phatu's 03 and 01 Gate 6 chip row includes "Free Hit restoration if selected". Under our rules there is no Free Hit. N/A, listed under decisions.

## Decisions for the team

1. **Target gameweek required by default** (`Fantasy:RequireTargetGameweek = true`). This is a breaking change for clients that don't yet send it; the setting can turn it off during the rollout. The legacy gameweek number is still accepted in place of the id.
2. **How writes are serialised:** every write locks the team row first (`SELECT … FOR UPDATE`) and the entry version is an EF concurrency token. A second device's write waits, then gets `409 stale_squad`. Deadlocks and serialisation failures are also returned as a retryable `409 stale_squad`, not 500.
3. **Receipts:**
   - Transfer confirmations get stored receipts with idempotency.
   - Lineup, chip and the legacy `POST /transfers` writes return the new squad and version but no stored receipt: they are idempotent by content and guarded by `If-Match`.
   - Extending receipts to them is a small change if wanted.
4. **Deadline lock trigger:** the API has no scheduler (Postgres is the only handoff, and the API runs no background jobs). The immutable snapshot is written:
   - by `POST /admin/gameweeks/{id}/lock`, the operator's deadline step, which also reconciles;
   - automatically when a gameweek is finalised.

   Writes are refused after the deadline anyway, so a later lock still captures the state at the deadline.
5. **Quotes:**
   - They last 15 minutes (`Fantasy:TransferQuoteMinutes`) and never past the deadline.
   - `priceRevision` is the newest `app.player_price_changes.id` for the season.
   - Confirmation also compares the actual sale and purchase totals.
6. **Chip cancellation:**
   - Bench Boost and Triple Captain can be cancelled before their gameweek's deadline; Wildcard can't, once played.
   - Rows are marked `cancelled_at`, never deleted, and are reused when a chip is played again.
   - A rare clash, where two cancelled rows block each other, returns `409 chip_not_cancellable` and needs support.
7. **Wildcard and hits:** Wildcard waives every hit already charged for its gameweek. The entry's `transfer_cost` becomes 0, and the receipts keep the original charges for audit.
8. **Free Hit restoration:** N/A (no Free Hit under our rules).
9. **Autopick:**
   - greedy: the most expensive affordable player per slot, keeping money for the cheapest remaining slots and the club cap;
   - default formation 4-4-2;
   - captain and vice are the two most expensive outfield starters.
   - It's a starting point, not a recommendation engine.

## Migration file names

- touchline-api: `docs/migrations/010_safe_writes.sql`
- touchline-sync: `db/migrations/V042__safe_writes.sql`

Not applied. Safe to rerun. Before 010, all existing writes work (with the new target-gameweek and version checks), while these need it:
- `POST /transfers/confirm` and chip cancellation return `503 Schema not ready`;
- the ledger shows no granted column;
- locks write no snapshot.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/3-accounts...sprint/4-safe-writes?expand=1 (base: `sprint/3-accounts`)
- touchline-sync: https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/3-migrations...sprint/4-migrations?expand=1 (base: `sprint/3-migrations`)
