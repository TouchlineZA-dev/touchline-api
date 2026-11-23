# Sprint 5 report: settlement, rankings and corrections

**Branches and status**
- **touchline-api:** branch `sprint/5-settlement`, stacked on `sprint/4-safe-writes`.
- **touchline-sync:** branch `sprint/5-migrations`, stacked on `sprint/4-migrations`.
- **Tests:** 393 passed, 0 skipped, 0 failed, including `Settlement/SettlementTests.cs` (10 tests on canonical Postgres). Sprint 2's reference totals (65, 61) and the replay tests still pass.

## "Pass when"

| Item | Evidence | Status |
|---|---|---|
| Totals match by player, manager and league | Per-player: `Every_reference_player_scores_what_the_hand_calculation_says` (Sprint 2, still passing). Manager: `A_recorded_resolution_finalises…` (51 / 45 by hand). League: `Standings_come_from_the_published_revision…` (ranks from the published revision) | done |
| Reruns are stable | `A_recorded_resolution_finalises_publishes_a_final_revision_and_a_rerun_is_identical` (a correction preview after finalising finds 0 changed teams and 0 rank changes; a second finalise is refused); `An_interrupted_run_resumes_without_double_counting` | done |
| The explain endpoint answers "why did my score drop?" | `GET /fantasy/team/points/explain?gameweek=N`; `A_rescinded_goal_is_removed_by_an_amendment_and_the_explain_view_says_why_the_score_dropped` ("goals 1 → 0", previous total, revision reason) | done |
| State machine open → locked → live → awaiting_review → final, stored with reason, input revision and time; only valid transitions | `SettlementStates.IsValid`; `app.gameweek_settlement` and `app.gameweek_state_transitions` (migration 011); admin `GET/POST /admin/gameweeks/{id}/settlement` | done |
| Completeness contract (Phatu's "fix the current disagreement") | `SettlementService.EvaluateAsync`, per-fixture outcomes: complete (worker evidence), verified_zero, manual_resolution, unattributed_resolved, unattributed_hold, awaiting_evidence (delayed feed), missing_data, late_facts, postponed, cancelled, abandoned, awarded, walk_over, not_finished. Never finalises on fixture states alone: `Without_worker_evidence_the_week_waits_for_review…`. The legacy gate also needs `marked_complete` now | done |
| Late facts after final → amendment revision | `amendmentNeeded` on the settlement view; `late_facts` fixture outcome; corrections workflow | done |
| Scoring: per fixture then aggregate; on-pitch intervals; inputs preserved; captain fallback after all the captain's fixtures; multiplier and fallback reasons recorded | `ScoreTeamsAsync` stores per-fixture breakdowns per player; `PickScore.Reason` is stored in every `points_results.breakdown` and revision | done |
| Provisional, final and amendment revisions with breakdowns; batched; atomic publication; interrupted runs resume without double counting | `app.score_revisions` and `app.score_revision_teams`; `BuildAndPublishAsync`; interruption test (batch size 1, interrupted after batch 1, resumes the same revision) | done (decision 2 on provisional revisions) |
| Rankings from the same revision; ties, late join, eligibility; paged and cached by revision + page; efficient "my rank"; movement; history | `StandingsView.revision`, `page`/`pageSize`/`totalRows`, `me`, `previousRank`/`movement`; cache keyed by revision; `GET /fantasy/team/history`; standings test | done (decision 4 on "my rank" efficiency) |
| Corrections: preview impact (players, teams, ranks), publish with source, author, reason and previous revision, correction message | `POST /admin/gameweeks/{id}/corrections/preview` and `/corrections`; `A_corrected_assist_changes_only_the_affected_contributions…` | done |
| Operations ownership doc | `docs/ops/fantasy-operations.md` | done |
| Test: repeat settlement identical | above | done |
| Test: corrected assist changes only the affected totals | above (the preview lists exactly the two players; both teams move by −2) | done |
| Test: rescinded goal removed, original and correcting revisions in history | rescinded-goal test; revision history `["final", "amendment"]` in the assist test | done |
| Test: sparse 0–0 vs missing data | `A_quiet_0_0_is_told_apart_from_missing_data` | done |
| Test: defender off at 60 before a later concession | `A_defender_off_at_60_before_a_late_concession_follows_the_whole_match_clean_sheet_rule` (decision 5) | done |
| Test: interrupted run → clean resume | above | done |
| Test: non-admin can't score, finalise or price (403) | `Non_admins_cannot_score_settle_correct_or_price` | done |
| Test: Sprint 2 reference totals still match | `ReferenceWeekTests` | done |

## Phatu items covered this sprint

- **03 Stage 5:** items 1–6 and the pass criteria.
- **01 Gate 7:** same as 03 Stage 5.
- **01 first tickets #5:** the worker/API completeness contract. A finished but incomplete game stays under review, and a verified quiet match can settle.
- **01 "Regression scenarios":**
  - rescinded red card or goal;
  - genuine 0–0;
  - defender off after 60;
  - captain misses one of two fixtures (Sprint 2's 41-point test);
  - finalisation stops midway.
- **01 "Matchday operations":** "Final whistles but insufficient scoring data" and "Correction after publication".
- **02 "Make scoring trustworthy":** finalizer and `gameweek_sync_status`; completeness for late facts and postponements; owners for creating, finalising, rescoring and applying prices; repeated scoring tests.
- **02 "Release gate":** a non-admin can't score; a final score and league rank match a hand calculation and stay stable after a rerun.
- **03 Stage 6 table:** finality state, revision and scoring explanations on points.

**Items I added that Part 2 didn't list:**
- **01 Gate 7.4:** "for a large season, calculate in batches" (`Settlement:BatchSize`, default 200).
- **03 Stage 5.6:** "issue a correction message if it changes a previously displayed result". The message is stored on the revision and returned with every final points view.

## What's left

- **Sprint 6:** OpenAPI examples; the league paging and visibility tests (public rank vs unpublished squad).
- **Sprint 7:**
  - load test of the most expensive standings request;
  - audit log for these admin actions (the transitions, resolutions and revisions already record actor and reason);
  - full gameweek replay through the public API.

## Needs sync team

1. **Populate `sync.gameweek_sync_status`** (0 rows in production). Until it's populated, every finished fixture needs an admin resolution before finalising.
2. **Record the evidence Settlement reads:** finished fixtures, events and lineups present, and stats present (`fixtures_complete`, `events_complete`, `stats_complete`, `marked_complete`).
3. **Keep `marked_complete` sticky** (it already is). After a late correction, the API detects changed facts itself through the input revision (`amendmentNeeded`).
4. **Exclude postponed fixtures from completeness** (default decision 1). This is still open on the worker side.
5. **Group fixtures by `app.fixture_gameweek_assignments` once V039 is applied** (carried over).

## Needs other owners

1. **Ops:**
   - run the deadline lock and the settlement routes per gameweek (`docs/ops/fantasy-operations.md`);
   - decide whether to automate them with a cron calling the admin routes.
2. **Support:** use `GET /fantasy/team/points/explain` for "why did my score drop?" tickets. An admin-side equivalent for any team arrives with the Sprint 7 access matrix.
3. **Sportmonks subscription:** still on the free plan. (Carried over.)

## Conflicts

1. **Phatu's 03 Stage 5.3** says "apply captain fallback **and ordinary autosubs** after all the player's eligible fixtures". Our rule is no autosubs. Captain fallback is applied after all fixtures; autosubs are not. Logged in Sprint 2 with the 42/34 vs 44/41 calculations.

## Decisions for the team

1. **Finalising without worker evidence:** a recorded admin resolution per finished fixture is required:
   - `verified_zero` for a quiet match;
   - `manual_resolution` for a checked match;
   - `unattributed_resolved` after the 48 h hold.

   A resolution is bound to the fixture's revision. If the facts change, the fixture shows `late_facts` and needs resolving again. There's no week-level "finalise anyway" switch.
2. **Provisional points aren't stored as revisions.** They're computed live from the same engine and labelled provisional. Only final and amendment revisions are stored (the `provisional` kind exists in the schema if the team wants snapshots).
3. **Unattributed goals and cards:** the 48 h hold (`Settlement:UnattributedHoldHours`) applies even when the worker has marked the week complete, measured from kickoff + match length + half-time.
4. **"My rank":** the overall standings query loads one row per team per finalised gameweek plus the cached live points. Pages are cached by revision. Measuring it at season scale is part of the Sprint 7 load test.
5. **Clean sheets:** whole-match rule, kept. The team concedes 0 in the match and the player has 60+ minutes. A defender who leaves at 60 before a late goal gets no clean sheet. FPL's pitch-time variant would award it. Change on request (a rulebook version bump).
6. **Rank movement:** the rank before the current gameweek's points, with the same tie rules.
7. **Standings caching:**
   - With final points only, a page is cached for 5 minutes per revision.
   - With provisional points, it follows the live-points cache (60 s).
   - A new team appears in Overall within 5 minutes.
8. **Amendments** rescore every team in the gameweek, but only changed teams show in the preview. The published revision keeps every team's breakdown, so any later explanation is reproducible.

## Migration file names

- touchline-api: `docs/migrations/011_settlement.sql`
- touchline-sync: `db/migrations/V043__settlement.sql`

Not applied. Safe to rerun. Before 011, `POST /admin/gameweeks/{id}/score` uses the legacy path, which now needs the worker's `marked_complete` and every fixture settled, and the settlement, resolution and correction routes return `503 Schema not ready`.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/4-safe-writes...sprint/5-settlement?expand=1 (base: `sprint/4-safe-writes`)
- touchline-sync: https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/4-migrations...sprint/5-migrations?expand=1 (base: `sprint/4-migrations`)
