# Final summary: API Sprints 1–7

**Run setup**
- **Mode:** A (stacked PRs).
- **Runbook:** v2 (Part 1b applied in Sprint 1).
- **Date:** 23 Nov 2026.

**Results**
- **Tests:** the suite on the final branch is **415 passed, 0 skipped, 0 failed**. The PostgreSQL tests ran locally against a throwaway PostgreSQL 17 server, built from the pinned canonical Flyway migrations, with the API connecting as `app_api_user`. CI runs them in Testcontainers with `TOUCHLINE_REQUIRE_DB_TESTS=true`. CI itself hasn't run on these branches yet: gh isn't installed here, so no PRs were opened, and CI runs when you open them.
- **Commits:** every commit is authored and committed by Leruo24, with no AI attribution lines.

## Sprint status

| Sprint | Status | touchline-api branch | touchline-sync branch | Report |
|---|---|---|---|---|
| 1 Football facts | pushed | `sprint/1-football-facts` | `sprint/1-migrations` | `sprint-1-report.md` |
| 2 Season rules | pushed | `sprint/2-season-rules` | `sprint/2-migrations` | `sprint-2-report.md` |
| 3 Accounts | pushed | `sprint/3-accounts` | `sprint/3-migrations` | `sprint-3-report.md` |
| 4 Safe writes | pushed | `sprint/4-safe-writes` | `sprint/4-migrations` | `sprint-4-report.md` |
| 5 Settlement | pushed | `sprint/5-settlement` | `sprint/5-migrations` | `sprint-5-report.md` |
| 6 Frontend contract | pushed | `sprint/6-frontend-contract` | none (no migration) | `sprint-6-report.md` |
| 7 Operate and secure | pushed | `sprint/7-operate-secure` | `sprint/7-migrations` | `sprint-7-report.md` |
| 8 Sync alignment | pushed | `sprint/8-sync-alignment` | none (stack renumbered) | `sprint-8-report.md` |

## PR links in merge order

Open each PR with the base shown and merge with "Create a merge commit". After a sprint merges, retarget the next PR's base to `main`.

| # | Repo | PR (compare link) | Base |
|---|---|---|---|
| 1 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/main...sprint/1-football-facts?expand=1 | `main` |
| 1 | sync | https://github.com/TouchlineZA-dev/touchline-sync/compare/main...sprint/1-migrations?expand=1 | `main` |
| 2 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/1-football-facts...sprint/2-season-rules?expand=1 | `sprint/1-football-facts` |
| 2 | sync | https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/1-migrations...sprint/2-migrations?expand=1 | `sprint/1-migrations` |
| 3 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/2-season-rules...sprint/3-accounts?expand=1 | `sprint/2-season-rules` |
| 3 | sync | https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/2-migrations...sprint/3-migrations?expand=1 | `sprint/2-migrations` |
| 4 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/3-accounts...sprint/4-safe-writes?expand=1 | `sprint/3-accounts` |
| 4 | sync | https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/3-migrations...sprint/4-migrations?expand=1 | `sprint/3-migrations` |
| 5 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/4-safe-writes...sprint/5-settlement?expand=1 | `sprint/4-safe-writes` |
| 5 | sync | https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/4-migrations...sprint/5-migrations?expand=1 | `sprint/4-migrations` |
| 6 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/5-settlement...sprint/6-frontend-contract?expand=1 | `sprint/5-settlement` |
| 7 | api | https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/6-frontend-contract...sprint/7-operate-secure?expand=1 | `sprint/6-frontend-contract` |
| 7 | sync | https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/5-migrations...sprint/7-migrations?expand=1 | `sprint/5-migrations` |

## Migration versions in order (none applied)

**Before ours:** the sync owner's V039–V041 (on touchline-sync `main`) and V042 (his branch `sprint/3-4-live-freshness-corrections`). His V042 PR must merge before ours. Ours were renumbered from V039–V044 to V043–V048 in Sprint 8 (`sprint-8-report.md`).

| touchline-api | touchline-sync | What |
|---|---|---|
| 007_gameweek_calendar.sql | V043 | persisted gameweek numbers; fixture→gameweek assignment history |
| 008_rules_versions.sql | V044 | rules and points-table versions on entries and points |
| 009_accounts.sql | V045 | optional email, usernames, favourite club, suspension and deletion, rename history, notification preferences |
| 010_safe_writes.sql | V046 | receipts and idempotency, granted transfers, locked snapshot, chip cancellation |
| 011_settlement.sql | V047 | state machine, fixture resolutions, score revisions |
| 012_admin_audit_log.sql | V048 | admin audit log |

**About these migrations:**
- Each one is safe to rerun and grants no DELETE.
- The API works before and after each is applied; new features return `503 Schema not ready` until their migration is in.
- **After merging each sync PR:** `git pull`, `flyway info`, then `flyway migrate`.

## Combined: needs sync team

1. **Data has stopped:**
   - The event and lineup sync stopped on 1 Nov.
   - PSL data stopped entirely on 18 Nov (subscription; see "needs other owners").
2. **Scheduler:** run the Python worker and retire the Netlify football schedules at one recorded cutover. Netlify is the real writer today, with no job rows or locks, and it doesn't write the gate.
3. **Completeness evidence:**
   - Populate `sync.gameweek_sync_status` (0 rows today). Until then every finished fixture needs an admin resolution before finalising.
   - Exclude postponed fixtures (state 10) from completeness.
   - Once V043 is live, group fixtures by `app.fixture_gameweek_assignments`.
4. **Reissued corrections:** mark the stale `match_events` row `rescinded = true` (preferred) or delete it. The API handles both.
5. **Schema gaps:**
   - Add a Flyway migration for `football.standings`, which exists in production with no migration.
   - Document the manual `player_match_stats.player_id` column, which is always NULL.
6. **Over-privileged worker role:** revoke `sync_worker_user`'s SELECT on `app.users`, `app.fantasy_teams` and `app.fantasy_squads`, and its INSERT and UPDATE on `app.points_results`, `app.player_prices` and `app.leaderboards` (live check, 23 Nov).
7. **Credentials:** rotate the `sync_worker_user` password (committed in V001), and remove passwords from V001.
8. **Guide updates:**
   - BACKEND_INTEGRATION.md: budget 100.0, price default 4.0, all 25 states, the gate exists.
   - The sync report's §1.3: own goal is 15, not 16.
   - Fill the missing short codes for 5 clubs.
9. **Provider timestamp:** store Sportmonks' own update time if available, so provider delay can be measured.
10. **Repo hygiene:** fix touchline-sync's 14 Dependabot alerts (4 critical) and run a full-history secret scan.

## Combined: needs other owners

1. **Sportmonks account holder:** the key is on the free plan, and there has been no PSL data since 18 Nov. Restore the subscription. The API keeps serving the last data with stale flags.
2. **Ops:**
   - rotate `app_api_user` (password in the sync repo's V001);
   - a staging load test (`docs/load`);
   - a timed restore;
   - a rotation rehearsal;
   - production-role access tests;
   - run the deadline lock and settlement routes per gameweek, or automate them;
   - a log retention setting.
3. **Supabase dashboard owner:**
   - `docs/accounts/provider-checklist.md` (staging and production credentials, callbacks, Apple secret rotation owner, Facebook live, linking);
   - MFA for admins;
   - two staging kit accounts.
4. **Mobile owner:**
   - the callback round-trip sign-off;
   - the staging kit flow;
   - a push provider for `docs/api/delivery-proposal.md`.
5. **Web owner:** `docs/api/web-migration.md`:
   - `kickoffUtc`;
   - retire synthetic ratings;
   - `gameweekId` and idempotency keys;
   - the club slug map;
   - stop storing passwords in localStorage.
6. **News-service owner:** decide on `docs/api/news-proposal.md`.
7. **Support:** `legacy_email_unlinked` linking, Supabase Auth deletion within 30 days, and using `/points/explain`.
8. **Legal and product:** image hotlinking and retention rights; storing provider-derived fixtures in git (replay pack, GW9 snapshot); rulebook sign-off.
9. **Privacy officer:** the POPIA review (`docs/security/privacy-inventory.md`).
10. **Independent security reviewer:** review and retest.

## Combined: conflicts (our rule kept)

| Source | Says | Ours |
|---|---|---|
| Phatu 01/03 reference calculation | FPL rules: captain ×2, autosubs → **42 / 34** | captain ×3, vice ×2 (×3 if the captain plays 0 all week), no autosubs → **44 / 41** (both tested) |
| Phatu 03 Stage 5.3 | "captain fallback **and ordinary autosubs**" | no autosubs |
| Phatu 02 shared-team gate | R300m budget | R100.0m |
| Phatu 03/01 chips | Free Hit restoration | no Free Hit (N/A) |
| BACKEND_INTEGRATION | missed penalty negative; don't score assists; lineup = appearance (no minutes); captain doubles; budget 300 | no missed-penalty deduction; assist +2; minutes from XI, subs and reds; captain ×3; R100m |
| Reviewed API (not Part 3) | team name 1–50 chars; token without email → 400 | 3–30 chars; email optional (per runbook) |

## Combined: decisions for the team

**From runbook Part 1b (defaults, open to overturn):**
1. **Postponed fixtures:** the week doesn't wait. Reassign the fixture on reschedule; it stays unassigned without a date.
2. **Reissued corrections:** support both delete and rescind (rescind preferred); the revision changes.
3. **Saves and defensive contributions:** no action.
4. **Unattributed goals and cards:** a 48 h hold, then an admin resolution; they count for the team, not a player.
5. **Scheduler:** the API doesn't care who writes; the worker cutover is recommended.

**Sprint 1:**
- `AWAITING_UPDATES` stays "scheduled" with `needsReview`.
- Suspended is not settled.
- Awarded and walk-over: final, with no player points.
- Interrupted: live, with the clock paused.
- New status `Unknown` for unrecognised codes.
- Freshness: 75 min inside the window, or the window closed and not settled.
- Revision id: a content hash.
- Standings policy: derived figures plus official deductions.

**Sprint 2:**
- Version strings `2026-27.v1` / `2026-27.points.v1`; finalising never rescores another version.
- Late entry: the team's first squad week.
- Deadline edits only before the deadline.
- Clean sheet: the whole-match rule (no pitch-time variant).
- A missing assister means no assist.
- Reference squads priced at a flat R6.0m.

**Sprint 3:**
- Generated usernames count as "not chosen".
- Reserved list; 30-day cooldown.
- A stored email is never overwritten.
- Recent sign-in: 10 min for export and delete.
- Deletion: ownership passes to the oldest active member or the league is archived; `auth_user_id` is kept.
- Suspension: hidden from Overall only.
- Problem codes on `/me`.

**Sprint 4:**
- Target gameweek required by default (a rollout switch exists).
- Team-row lock plus version concurrency token.
- Receipts for transfer confirmations only.
- The deadline lock runs via the admin route and on finalising (no scheduler).
- 15-minute quotes; `priceRevision` = the newest price change.
- Chip cancellation for Bench Boost and Triple Captain before the deadline (rows marked, not deleted).
- Wildcard waives charged hits.
- Greedy autopick.

**Sprint 5:**
- An admin resolution per fixture when there's no worker evidence.
- No stored provisional revisions.
- The hold applies even with worker evidence.
- "My rank" efficiency is measured in the load test.
- Rank movement is relative to before this week.
- Standings cache: 5 min final, 60 s live.
- Amendments rescore every team.

**Sprint 6:**
- Examples live in `examples.md`; typed schemas are a follow-up.
- "Follow" means ETag revalidation until delivery exists.
- `kickoff` alias until the web switches.
- Opt-in list paging.
- Minimal `/health`, with freshness stale after 48 h.
- The staging kit is never on in Production.
- OpenAPI is exposed by setting.

**Sprint 7:**
- Support and operator share the admin role.
- Rate-limit values.
- Public cache: 15 s.
- Startup validation in Staging and Production only.
- CI secret scan from 23 Nov, plus a one-off full-history scan by ops.
- Audit writes never fail requests.

## Phatu coverage totals (`docs/sprints/PHATU-COVERAGE.md`)

| Status | Count |
|---|---|
| done | 89 |
| partial | 5 |
| not done | 0 |
| other owner / N/A / conflict | 3 |
| **Total API-owned items** | **97** |

**Partial items (all blocked on other owners or a staging run):**
- #22: season-rollover rehearsal;
- #25 and #82: delivery (proposal only);
- #37: news (proposal);
- #84: the frontend developer's staging walkthrough.

**Other owner or conflict:**
- #24: news;
- #60: provider setup;
- #54: the 42/34 vs 44/41 sign-off.

## Human launch checklist (runbook Part 4; Claude Code didn't do these)

**Leruo / Phatu**
- [ ] Sign off the rulebook and every FPL difference, including the captain/autosub conflict: our 44/41 vs the handbook's 42/34.
- [ ] Resolve the "conflicts" and "decisions for the team" lists.
- [ ] Second reviewer reproduces the two reference-team totals (65, 61; `docs/rules/reference-week.md`).

**Sync team**
- [ ] Fix the stopped event sync (since 1 Nov).
- [ ] Backfill 2025/26 lineups.
- [ ] Populate `sync.gameweek_sync_status`.
- [ ] Record the evidence Sprint 5 lists.
- [ ] Identify the active scheduler (it's Netlify; cut over to the worker).

**Mobile / Supabase**
- [ ] Real staging callbacks for Google, Apple and Facebook, including linking, recovery and a second device. The mobile owner signs off the round trip.
- [ ] MFA for production admins.

**Ops**
- [ ] Rotate all leaked keys (both DB role passwords in V001).
- [ ] Timed restore in an isolated environment.
- [ ] Deadline load test in staging.
- [ ] Production-role access tests (re-run `docs/security/effective-permissions.sql` after revoking the worker's extra `app` access).
- [ ] Secret rotation rehearsal.

**Joint sign-off**
- [ ] The worker writes a goal and corrects its scorer in Postgres.
- [ ] The API shows the right score, timeline, fantasy points and league positions.
- [ ] Stop the worker: reads continue, staleness shows, and finalisation waits for missing data.

**Release**
- [ ] Two consecutive live PSL gameweeks in staging or closed beta, with reports.
- [ ] Independent security review: findings fixed and retested, remaining risks approved in writing.
- [ ] POPIA review by the responsible person.
- [ ] Fill in and sign `docs/release/release-record.md` (go / hold / rollback, who decided, when).

## Pull requests (opened with gh, 23 Nov 2026), in merge order

| Order | touchline-api | touchline-sync |
|---|---|---|
| 0 | – | the sync owner's V042 (branch `sprint/3-4-live-freshness-corrections`, no PR yet; his V039–V041 merged as #3). V043 is already on sync main, so his V042 must be renamed V049 (or applied with outOfOrder) |
| 1 | #6 Sprint 1 (merged) | #4 V043 (merged) |
| 2 | #14 Sprint 2 (replaces #7, auto-closed when its base branch was deleted) | #10 V044 (replaces #5, auto-closed when its base branch was deleted) |
| 3 | #8 Sprint 3 | #6 V045 |
| 4 | #9 Sprint 4 | #7 V046 |
| 5 | #10 Sprint 5 | #8 V047 |
| 6 | #11 Sprint 6 | – |
| 7 | #12 Sprint 7 | #9 V048 |
| 8 | #13 Sprint 8 | – |

Every touchline-api PR now targets `main` (rebased onto main after #6 merged), so no PR depends on a branch that gets deleted on merge. The touchline-sync PRs #6–#9 still target the previous `sprint/n-migrations` branch: retarget each to `main` before its parent merges. Deploy the API through #13 (the `withdrawn_at` filter) before V040 runs in production.
