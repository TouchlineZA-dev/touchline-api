# Sprint 2 report: establish the season rules

**Branches and status**
- **touchline-api:** branch `sprint/2-season-rules`, stacked on `sprint/1-football-facts` (Sprint 1 isn't merged yet).
- **touchline-sync:** branch `sprint/2-migrations`, stacked on `sprint/1-migrations`.
- **Tests:** 353 passed, 0 skipped, 0 failed, including the PostgreSQL-backed reference-week and replay tests.

## "Pass when"

| Item | Evidence | Status |
|---|---|---|
| Every FPL difference listed for product sign-off | `docs/rules/rulebook-2026-27-v1.md` §15 (12 capabilities, one row each); `GET /api/v1/rules` → `differencesFromFplClassic`; `Rules_endpoint_publishes_version_2026_27_v1…` | done; sign-off is a human item |
| Every scoring category has an attributable input, a worked example, a missing-data policy and a passing test | `docs/rules/coverage-matrix.md` (minutes, goal, assist, clean sheet, yellow, red/second yellow, own goal; saves, defensive contributions and penalties marked unsupported) | done |
| Reference totals match | `docs/rules/reference-week.md`: Squad A **65**, Squad B **61**, each hand-calculated. The independent `tests/db/reference/tools/score_gw9.py` gives the same. `Reference_squad_totals_match_the_hand_calculation` (engine) and `Every_reference_player_scores_what_the_hand_calculation_says` (30 players) | done; the second human reviewer is a human item |
| Past entries keep their rules version | migration 008 / V040; finalising stamps `rules_version` and `points_table_version`; `Finalising_stamps_the_rules_version_and_a_new_version_never_rescores_the_past`; `TeamPointsView.rulesVersion` | done; applies when 008 is run (the code works before it) |
| Rulebook doc + `GET /api/v1/rules` (version `2026-27.v1`) covering ties, late entry, reassignment, corrections, chip periods, sale-price rounding | rulebook §1–§14; `Fantasy/Rulebook.cs` (built from `FantasyRules`, so it can't drift from the code) | done |
| Deadline: exact instant in UTC + SAST; history protected; when a future deadline may change | `GameweekRef.deadlineSast`; `PUT /admin/gameweeks/{id}/deadline` refuses a passed deadline; `A_passed_deadline_never_changes…`; `Deadline_is_published_in_utc_and_sast` | done |
| Late entry: starts at the next open deadline, unlimited transfers before its first deadline, no earlier points | `TransferRules.UnlimitedFreeTransfers(…, targetIsTeamsFirstGameweek)`; `FantasyTeamService.IsTeamsFirstGameweekAsync`; `Late_entrants_get_unlimited_free_transfers…`. No earlier points because there are no earlier entries | done |
| Corrections policy documented (implemented in Sprint 5) | rulebook §12 | done (doc) |
| Reference week GW9 with two squads (captain doesn't play; Triple Captain) | `tests/db/reference/gw9.json`, `gw9-squads.json`, `reference-week.md` | done |
| Replay steps 0–4b mirrored in API tests (4b with soft rescind and hard delete) | `Reference/ReplayTests.cs` (3 tests, 4 cases) | done |
| Rule tests under our rules: 44 and 41 | `Captain_plays_zero_vice_scores_six…_is_44`, `Captain_plays_only_the_second_fixture…_41` | done |

## Phatu items covered this sprint

- **03 Stage 2:**
  - rulebook and versioning;
  - the FPL table, all 12 rows;
  - the pass criteria;
  - the deadline row;
  - the scoring row (versioned points table, reliable inputs only, assist attribution and on-pitch intervals confirmed);
  - first ticket #1 (rulebook, two sample teams against the worker owner's reference week).
- **01 Gate 1:**
  - 1.2–1.3: coverage matrix;
  - 1.4: rules approved for sign-off, version pinned to locked entries, machine-readable rulebook;
  - the pass criteria (independent example per category, second-reviewer-reproducible reference teams).
- **01:** first tickets #1 and #2, and the "Reference gameweek" section.
- **02 "Make scoring trustworthy":** the corrected goal is hand-checked against the real stored fixture 1393 replay (completes Sprint 1's partial item 30).

**Items I added that Part 2 didn't list:**
- **01 Gate 1:** "Record each choice against a season". The rulebook carries `season: 2026/2027`.
- **03 Stage 2:** "A manager should be able to read the rules before picking a squad". `/rules` is public and needs no sign-in.

## What's left

- **Sprint 3:** username and team-name rules join the rulebook (moderation, cooldown).
- **Sprint 4:** chip activation and cancellation policy.
- **Sprint 5:**
  - revisions, amendment publication, audited re-score;
  - unattributed-event hold window;
  - flagging finalised gameweeks for rescore after a fixture move;
  - the pitch-time clean-sheet test.
- **Human:** product sign-off of the rulebook and the FPL differences; a second reviewer reproduces 65 and 61.

## Needs sync team

1. **Sprint 1's list still applies:**
   - the standings migration;
   - the completeness writer reading assignments;
   - excluding state 10;
   - marking reissued events rescinded;
   - the scheduler cutover;
   - the stopped event sync;
   - guide updates.
2. **Sync report §1.3 corrections:**
   - "Own goals = event 16" is wrong (15 is own goal, 16 is penalty goal).
   - Also, "Goals, assists: events 14/15" should be 14/16.
3. **Replay pack, step 3:** step 3 has 6 lineup rows and no 75' Mbuthuma substitution, while production has full lineups and the 75' sub. That's fine for the replay, but the pack's step 3 points differ from the real fixture (Mbuthuma 5 in the pack vs 4 in production). Worth a note in the pack's `_meta`.
4. **Dependabot:** GitHub reports 14 open Dependabot alerts on touchline-sync's default branch (4 critical, 6 high).

## Needs other owners

1. **Sportmonks subscription:** still on the free plan; no PSL data since 18 Nov. (Carried over.)
2. **Sign-off:** product sign-off of `docs/rules/rulebook-2026-27-v1.md`.
3. **Second reviewer:** someone other than Leruo reproduces the reference totals 65 and 61 from the rulebook alone.
4. **Data retention:** retention and use rights for storing provider-derived fixture data in git (`tests/db/reference/gw9.json`, the replay pack). The sync owner flagged the same question.

## Conflicts

**Phatu's handbook reference calculation** (01 "Reference gameweek", 03 Stage 5) uses FPL Classic rules. Our rules (runbook Part 3) are kept. The same inputs, side by side:

| Inputs | FPL rules (Phatu) | Touchline rules (ours) |
|---|---|---|
| Captain A 0 min, vice B 6, bench C 4 (eligible), nine others 30, −4 hit | 30 + 0 + **B × 2 = 12** + **autosub C 4** − 4 = **42** | 30 + 0 + **B × 3 = 18** (vice takes the captain's ×3) + **C 0** (no autosubs) − 4 = **44** |
| A plays only the 2nd fixture of a double week, scores 1 in total; rest unchanged | 30 + **A × 2 = 2** + **B 6** (vice ×1 when the captain plays) − 4 = **34** | 30 + **A × 3 = 3** + **B × 2 = 12** − 4 = **41** |

**Why the totals differ:**
- **Captain multiplier:** FPL ×2, ours ×3.
- **Vice when the captain plays:** FPL ×1, ours ×2.
- **Vice taking the armband:** FPL ×2, ours ×3.
- **Autosubs:** FPL yes, ours no.

Both of our figures are asserted in tests. For sign-off: Part 4, "Sign off the rulebook… including the captain/autosub conflict".

## Decisions for the team

1. **Rules version strings:** `2026-27.v1` for the rulebook, `2026-27.points.v1` for the points table.
   - Backfill: rows finalised before 008 are stamped `2026-27.v1`, because those rules applied.
   - "Never rescore the past" means finalising skips an entry scored under another version. A deliberate re-score under new rules would need an explicit admin action, which doesn't exist.
2. **Late entry:** "the team's first gameweek" means the first gameweek in which the team has any squad.
   - Transfers there are free until its deadline.
   - The initial squad pick (`PUT /squad`) was already unlimited.
3. **Deadline edits:** allowed only before the current deadline passes, never later than the first kickoff, and with a reason. A persistent audit row arrives with the Sprint 7 audit log; until then the reason is required but only returned, not stored.
4. **Clean sheet:** "team conceded 0 in the whole match" for players with 60+ minutes (current behaviour, rulebook §9). The FPL variant counts only goals conceded while the player was on the pitch. Sprint 5 tests the case and keeps the whole-match rule unless the team decides otherwise.
5. **Assists with no related player** count as "no assist", not "missing": the provider can't tell them apart. `Fantasy:AssistsEnabled` stays on, as agreed.
6. **Reference squads:** priced at a flat R6.0m because the GW9 snapshot has no prices. Legality covers positions, club cap, formation and bench.

The five Part 1b default decisions stand as listed in `sprint-1-report.md` and are reflected in the rulebook (§11, §12).

## Migration file names

- touchline-api: `docs/migrations/008_rules_versions.sql`
- touchline-sync: `db/migrations/V040__rules_versions.sql`

Not applied. Safe to rerun.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/1-football-facts...sprint/2-season-rules?expand=1 (base: `sprint/1-football-facts`)
- touchline-sync: https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/1-migrations...sprint/2-migrations?expand=1 (base: `sprint/1-migrations`)
