# Touchline API: sprint runbook (Stages 1–7, back to back)

> **For Leruo (not Claude Code):**
> 1. Save this file as `touchline-api/docs/sprints/RUNBOOK.md`. Put Phatu's three docs in `touchline-api/docs/plans/`:
>    - `01-Touchline-Backend-Launch-Handbook.md`
>    - `02-platform-api.md`
>    - `03-Leruo-API.md`
> 2. Start Claude Code in the touchline-api folder, attach this file and paste:
>    `Execute this runbook in MODE A from Sprint 1. Save it to docs/sprints/RUNBOOK.md first.`
>    (Use `MODE B` instead for one big PR at the end.)
> 3. If the session stops or runs out of context, start a new one and paste:
>    `Read docs/sprints/RUNBOOK.md and docs/sprints/PROGRESS.md and continue from where PROGRESS.md says.`
> 4. **Mode A:**
>    - Merge each sprint's PRs **in order** while it works on the next sprint.
>    - Use **"Create a merge commit"** (NOT squash) and tick "delete branch".
>    - After merging a touchline-sync PR, run `git pull`, `flyway info`, then `flyway migrate` in touchline-sync.
> 5. **Mode B:** you get one PR per repo at the end with one commit per sprint. Merge both, then run Flyway once.

---

## Part 1: Instructions for Claude Code

You are implementing **everything Phatu assigned to the API** in `docs/plans/`, Sprints 1 to 7 back to back, **without waiting for me between sprints**:
- **`03-Leruo-API.md`:** the main plan. Its Stages 1–7 are Sprints 1–7.
- **`02-platform-api.md`:** Phatu's platform API plan, folded into the sprints below.
- **`01-Touchline-Backend-Launch-Handbook.md`:** the API-owned parts of Gates 1–9, the regression scenarios, matchday operations and the release record. These are folded into the sprints below.

The sprint text in Part 2 already includes Phatu's items. **Also read all three docs yourself.** If you find an API-owned item in them that Part 2 missed, add it to the matching sprint and say so in that sprint's report.

I merge PRs and run migrations in parallel with your work.

### Modes

**MODE A (stacked PRs, keep going):**
- **Branching:**
  - Sprint n uses branch `sprint/<n>-<name>`.
  - Run `git fetch` first. If Sprint n−1 is already merged, branch from `origin/main`; otherwise branch from `sprint/<n-1>-<name>`.
- **When a sprint is done:**
  - Commit and push.
  - Open the PR with its base set to the previous sprint branch. Use `main` instead for Sprint 1, or if the previous sprint is already merged.
  - Update PROGRESS.md, then go straight to the next sprint.
- **touchline-sync:** stack branches the same way (`sprint/<n>-migrations`). Open one PR per sprint that has migrations.
- **Before each sprint:** run `git fetch`. If the previous sprint's PR has been merged, rebase your new work onto `origin/main`.

**MODE B (one branch, everything):**
- Use `sprint/all-stages` in touchline-api, plus `sprint/all-migrations` in touchline-sync if any sprint adds a migration.
- Make one commit per sprint (`Sprint <n>: <stage name>`) and push after each.
- Open one PR per repo after Sprint 7.

### Checks before Sprint 1
1. Run `git fetch`. `audit/fantasy-2026-11-23` and `plan/platform-api-status` must be merged into main (0 commits ahead). If either isn't, STOP and tell me.
2. `docs/plans/03-Leruo-API.md` and `docs/plans/02-platform-api.md` must exist. If either is missing, STOP and tell me.
3. In Sprint 1, commit this RUNBOOK and the plan docs if they aren't tracked yet.

### Rules for every sprint (save a copy to `docs/sprints/SPRINT_RULES.md` in Sprint 1)

1. **Postgres is the only handoff.**
   - The API never calls Sportmonks or the sync worker, including during outages, manual refreshes and settlement.
   - The only outbound call allowed is to the Supabase JWKS endpoint.
2. **Follow touchline-sync's `BACKEND_INTEGRATION.md`.**
   - If it, or one of Phatu's docs, conflicts with an agreed Touchline rule (Part 3), keep OUR rule.
   - Log the conflict under "conflicts" in that sprint's report and continue.
3. **Database limits:**
   - The API connects as `app_api_user`.
   - No EF migrations and no DDL from the API.
   - Never write to the `football` or `sync` schemas.
   - **No DELETE on app tables.** Use anonymisation, archiving or soft-delete flags. If something truly needs a hard delete, log it under "decisions for the team" and use soft-delete for now.
4. **Schema changes:**
   - Write the next Flyway version in `touchline-sync/db/migrations` (the live DB is at V038; continue after the highest version on main and your stacked branches).
   - Mirror it in `touchline-api/docs/migrations/`.
   - Make both rerunnable: IF NOT EXISTS, guarded ALTERs, idempotent grants.
   - **Never apply migrations.**
   - Code must not crash if a new column isn't live yet.
5. **Never change an agreed rule** (Part 3). Proposals go under "decisions for the team".
6. **Work for other teams:**
   - Sync-worker changes go under "needs sync team".
   - Mobile, Supabase dashboard, news-service, legal/product or ops items go under "needs other owners".
   - Don't do these yourself.
7. **Tests for everything.**
   - Postgres-backed tests run in CI, since Docker isn't installed locally. Skip them locally and make them required in CI.
   - The whole suite must pass before every push.
8. **Secrets:** never print, log or commit them. `.env` stays git-ignored.
9. **Git identity:**
   - Every commit is authored and committed by **Leruo24 <mtller042@myuct.ac.za>**.
   - No Claude/AI author or co-author, and no "Co-Authored-By", "Generated with" or "Claude-Session" lines in commits or PR descriptions.
   - Check with `git log -1 --format=full` before each push.
10. **PRs:** use `gh pr create` if gh is logged in; otherwise print the compare link. Never merge anything.
11. **Report:** write `docs/sprints/sprint-<n>-report.md`, containing:
    - a "Pass when" table: item | evidence (test name, endpoint output or file) | status (done / partial / blocked);
    - Phatu items covered this sprint (doc + section);
    - what's left;
    - needs sync team;
    - needs other owners;
    - conflicts;
    - decisions for the team;
    - migration file names;
    - PR links.
12. **Progress:** update `docs/sprints/PROGRESS.md` after every sprint, recording:
    - mode;
    - current sprint;
    - each sprint's status (not started / in progress / pushed / merged);
    - branches;
    - PR links;
    - migration versions;
    - blockers.

    If you're resumed in a new session, read PROGRESS.md first.
13. **Phatu coverage:** keep `docs/sprints/PHATU-COVERAGE.md` up to date. Every API-owned item from the three plan docs goes in it as: doc | section | item | sprint | evidence | status. The aim is for nothing Phatu wrote for the API to go missing.

### When to stop vs keep going

**Keep going** in these cases. Log the item and keep current behaviour:
- a "Pass when" item can't be fully proven in code (mark it partial);
- something needs the sync team, other owners or a team decision;
- a doc conflicts with our rules.

**Stop and tell me** when:
- tests are failing and you can't fix them after a serious attempt;
- a push is rejected, or a branch won't rebase cleanly;
- you need a secret you don't have;
- the work would break rule 3 or change an agreed rule;
- the checks before Sprint 1 fail.

When you stop, write the exact blocker in PROGRESS.md.

### At the end
After Sprint 7, write `docs/sprints/FINAL-SUMMARY.md` with:
- every sprint's status;
- all PR links in merge order;
- all migration versions in order;
- the combined lists: needs sync team, needs other owners, conflicts, decisions;
- PHATU-COVERAGE.md totals (done / partial / not done);
- the human launch checklist (Part 4).

Then show me that summary.

---

## Part 1b: Facts from the sync owner's Sprint 1 report (23 Nov 2026) that change the API work

The sync owner checked production read-only, at touchline-sync `fbf5d5d`. Treat these as facts about the data and build for them.

**Who writes the data**
- **The real writer is Netlify, not the Python worker.** Netlify runs `sync-fixtures` hourly, `sync-daily` at 00:00 UTC and `sync-news` every 3 h. The Render worker has never run as the scheduler on this DB.
- **`sync.gameweek_sync_status` has 0 rows.**
  - The Sprint 5 completeness gate must work while it's empty: with no evidence, a week stays `awaiting_review`, and only an admin manual resolution (recorded with reason) can finalise it.
  - Never finalise on "all fixtures final" alone.
- **PSL data stopped on 18 Nov:** the Sportmonks key is on the free plan. The API must keep serving the last known data, with stale flags. Nothing in the API can or should "fix" this. It goes under "needs other owners".

**Event and state codes** (checked against Sportmonks v3 docs)
- Events: 14 goal, **15 own goal**, 16 penalty goal, 17 missed penalty, 18 substitution, 19 yellow, 20 red, 21 second yellow, 10 VAR.
  - **The sync report's section 1.3 says "own goals = event 16". That's wrong per Sportmonks.** Keep the API's existing mapping (goals 14 + 16, own goal 15) and add a regression test so a penalty goal scores +3 and an own goal −2.
  - For goals, `related_player_id` = the assist.
  - For subs, `player_id` = player ON and `related_player_id` = player OFF.
- States: 1 NS, 2 first half, 3 HT, 4 break, 5 FT, 6 ET, 7 AET, 8 FT_PEN, 9 penalties, 10 postponed, 11 suspended, 12 cancelled, 13 TBA, 14 walkover, 15 abandoned, 16 delayed, 17 awarded, 18 interrupted, 19 awaiting updates, 20 deleted, 21 ET break, 22 second half, 23 ET second half, 25 penalty break. Map every one of them in the Sprint 1 state matrix.

**Missing IDs.** Scoring and match detail must handle null or unknown player IDs without crashing:
- 296 lineup rows this season have no player id;
- 8 goals this season have no scorer;
- 44 subs have no on-player, and 46 on-players aren't in that fixture's lineup;
- 75 yellow cards have no player;
- 4 scorers aren't in the lineup.

Handling rules:
- The event still counts for the team score, and so for clean sheets.
- Points go to no one.
- The fixture's coverage shows "unattributed events: n".
- An unattributed goal or card keeps the fixture under review (see default decision 4).

**What data exists**
- Team stats are team-level only, and zero values are often omitted, so treat absence as "unknown" unless the coverage matrix says otherwise.
- `football.player_match_stats.player_id` exists in production (all NULL, created by a manual change). Never read it.
- Player-level stats (saves, tackles, etc.) aren't ingested. Our scoring doesn't use them, so there's no launch impact. Mark them "unsupported" in the coverage matrix.
- Lineups arrive about 30 min before kickoff, which is after our 90-min deadline. That's expected. Pickability still uses "appeared in a current-season lineup".
- Player images are hotlinked from `cdn.sportmonks.com`. Return them as stored. Licence and retention questions go under "needs other owners".
- Minutes in extra time and stoppage are approximate; our capping rules apply.

**Reference week**
- **GW9, Orlando Pirates 2–1 Kaizer Chiefs:**
  - Sportmonks fixture 19781326, our fixture id **1393**, kickoff 31 Oct 13:30 UTC.
  - The worker's replay pack is in touchline-sync `tests/replay/fixture_19781326/`.
- Use GW9 as the Sprint 2 reference week. Mirror the replay steps in API tests:
  - step 0: lineups only, no score;
  - steps 1–2: goals and a sub;
  - step 3: FT 2–1, week pending until the 4 h settle window passes;
  - step 4a: the 24' goal is re-attributed to Mokobodi under the SAME event id → points move once;
  - step 4b: the goal is reissued under a NEW id. After the worker fix, the stale event is gone or marked rescinded. The API must show 3 goals and give no double points. Test both a hard-removed and a soft-rescinded old event.

**Default decisions** (the sync report asked Leruo; implement these defaults and list them under "decisions for the team" so Leruo or Phatu can overturn them):
1. **Postponed fixtures:**
   - The original gameweek does NOT wait for them; their outcome is "postponed".
   - When rescheduled, record a new fixture→gameweek assignment (Sprint 1 calendar) to the gameweek containing the new kickoff. Points count there, FPL-style blank then double.
   - With no new date, the fixture stays unassigned.
   - The worker should exclude state 10 from completeness: needs sync team.
2. **Reissued corrections:**
   - Support both: the worker hard-deleting stale `match_events`, or marking them rescinded (preferred, since it keeps correction history).
   - The API excludes rescinded or removed events, changes the event revision, and flags the gameweek for rescore.
   - The worker-side change is needs sync team.
3. **Saves and defensive contributions:** not in our rules, so no action.
4. **Unattributed goals and cards:**
   - The fixture stays `awaiting_review` for a hold window (a setting, default 48 h) so a later sync can attribute it.
   - After that, an admin can finalise via a recorded manual resolution: the goal counts for the score, and nobody gets the player points.
5. **Scheduler:** the API doesn't care who writes. Record the recommendation under "needs sync team": run the Python worker and retire the Netlify football schedules at one recorded cutover (never both at once). The worker has locking, job rows and the `is_current` season contract, and it's the one designed to write `gameweek_sync_status`.

---

## Part 2: The sprints

### SPRINT 1: Stage 1, read football facts consistently (`sprint/1-football-facts`)

**Step 0a: read the sync owner's integration guide.**
- Pull touchline-sync main (`C:\Users\leruo\Downloads\touchline\touchline-sync`) and read `BACKEND_INTEGRATION.md` fully.
- Write `docs/plans/backend-integration-alignment.md`. For each instruction, write: what the guide says | what the API does today | aligned / mismatch. Cover at least:
  - ID spaces, read-only schemas, grants and roles;
  - the gameweek_sync_status gate;
  - Sportmonks codes and final states;
  - timestamps and timezones;
  - player_match_stats being team-level;
  - placeholder images.
- Fix plain mismatches with tests; log conflicts.

**Step 0b: Phatu's platform fixes (from `02-platform-api.md`).**
1. **Test fixture:** `tests/Touchline.Api.Tests/Auth/PostgresFixture.cs` reads V033 from a deleted file. Change it to consume the canonical Flyway migrations from touchline-sync (`db/migrations`). Pin the location in config and copy them into the test setup in CI if needed.
2. **Migration tests:** run the auth and fantasy persistence tests on disposable Postgres with the REAL grants (`app_api_user`). Cover a clean install (all migrations from empty) and a populated upgrade.
3. **phathu-dev branch (read only):** don't merge or change it. Report what the 1 commit ahead on `phathu-dev` contains (`git log main..origin/phathu-dev`, `git diff main...origin/phathu-dev --stat`) and whether any of it is still needed. That includes the local `appsettings.Development.json` change: deployment settings stay in the environment, so nothing environment-specific gets committed.
4. **Sportmonks codes:** confirm `Business/SportmonksCodes.cs` has the starter (11), bench (12) and substitution-direction mappings filled in, with regression tests. Hand-check minutes, one substitute, captain points and a corrected goal against a real stored PSL fixture.

**Work:**
1. **One source of truth.**
   - One implementation of match status, phase and clock, shared by the fixture list, match detail and scoring.
   - Resolve the Sportmonks player IDs in match_events and player_lineups to internal football.players IDs in one place.
2. **Status mapping fix (Phatu's ticket):** first half and half-time currently fall through to "scheduled". Build one provider-state matrix that maps every state explicitly:
   - scheduled, first half, half-time, second half, stoppage;
   - interrupted, postponed, abandoned, finished;
   - extra time, penalties and awarded, where supported;
   - unknown provider codes are preserved and flagged for review, never silently mapped.

   Match tests and scoring tests both use this matrix.
3. **`GET /api/v1/fixtures/{id}` (match detail):**
   - teams, score, phase/status, clock source and kickoff (UTC);
   - ordered, paginated events (goals, assists, cards, subs; stoppage shown like 45+2);
   - lineups (XI and bench) and substitutions;
   - supported team/player stats;
   - coverage/capability flags per fixture (lineups / events / stats; unavailable vs actual zero);
   - freshness: last updated, server time, and a `stale` flag based on the match window (N minutes is a setting);
   - an event-set revision id (propose and document it).

   A scheduled match shows an unknown score (null), not 0–0.

   **Fixture list:** filters by date, competition and club.
4. **Timestamps:** expose what's available: provider timestamp, first observation, DB commit (updated_at) and API publication time. Label honestly what each measured interval actually covers.
5. **Tests** (03 Stage 1 table + handbook regressions):
   - **Goal in the first half:** the live phase and confirmed score, with the same goal in the fixture list, the detail and the scoring input.
   - **No lineup yet:** the fixture still opens, with "lineups: pending".
   - **Worker outage:** reads succeed, the response has stale=true and the last observation time, and there are ZERO outbound football requests.
   - **Corrected scorer with the score unchanged:** the timeline updates, the revision changes, and points move from the old scorer to the new one exactly once.
   - **Postponed or changed kickoff:** the football state is correct and the fantasy week is resolved separately. A late change can't rewrite a locked entry without a recorded decision.
   - **Same snapshot ingested 3 times:** one fixture and event set; the player gets points once.
   - **Phone reopening at half-time:** gets the same score and timeline as a continuously connected client.
   - **Fixture moved into another fantasy week:** points follow the approved assignment; history records the move.
6. **Calendar (handbook Gate 2):**
   - Stop deriving week numbers from ordering or SAST date windows at read time.
   - Persist an immutable gameweek id, a public number and the season.
   - Persist an explicit fixture→gameweek assignment, with revision history (when, why, who). This is a migration.
   - Backfill from the 28 seeded 2026/27 gameweeks.
   - Fantasy writes return server time, target gameweek id and deadline.
7. **Standings:** `GET /api/v1/standings` from football.standings:
   - position, played, W/D/L, GF/GA/GD, points, deductions and tie-break state;
   - document official vs derived figures and how discrepancies are resolved;
   - an awarded match or deduction is reflected without inventing goal events;
   - same filters and tie rules as the Netlify standings function.
8. **Architecture test:** no Sportmonks credentials and no worker URL configured. A test HttpMessageHandler fails on any host except Supabase JWKS.
9. **Pass when:**
   - a historical match is served completely from a populated DB;
   - corrections keep every view consistent;
   - the report explains how to measure the delay from worker commit to API visibility, separately from provider delay.

### SPRINT 2: Stage 2, establish the season rules (`sprint/2-season-rules`)
Use 2026/27 FPL Classic as the reference for where we differ. Our rules (Part 3) win.

1. **Rulebook:**
   - Write `docs/rules/rulebook-2026-27-v1.md` and add a read-only `GET /api/v1/rules` (JSON, version `"2026-27.v1"`).
   - It covers every rule in Part 3, including:
     - ties;
     - late entries;
     - fixture reassignments;
     - corrections;
     - chip periods;
     - sale-price rounding (money handled in decimal or integer tenths of a million, never float).
   - Add a table "differences from FPL Classic and why", with one row per capability in Phatu's 03 Stage 2 table: entry, selection, deadline, captain/bench, transfers, prices, chips, scoring, rankings, social, player tools, additional modes.
2. **Deadline:**
   - Publish the exact deadline instant (UTC + SAST).
   - Protect already-published history.
   - Document when a future deadline may change and how managers are told.
3. **Coverage matrix (handbook Gate 1):** write `docs/rules/coverage-matrix.md`. For each scoring category (minutes, goal, assist, clean sheet, cards, own goal) give:
   - the attributable player-level input in Postgres;
   - how confirmed zero, missing, delayed and unsupported are told apart;
   - which states block settlement;
   - what the API shows while waiting;
   - the missing-data policy.

   Confirm assist attribution and on-pitch intervals. Only categories with reliable player-level input are publishable. Team-level stats can't award player points.
4. **Versioning:** store the rules version and points-table version on each gameweek entry and scored result (migration). A new version never rescores the past.
5. **Late entry:** a team created after GW1 starts in the next gameweek whose deadline hasn't passed. It gets unlimited free transfers before its first deadline and no earlier points. Tests.
6. **Corrections:** document the policy (admin re-score only, audited, new revision). It's implemented in Sprint 5.
7. **Reference week:**
   - Use **GW9** (see Part 1b; the worker's replay pack covers fixture 1393), with the other GW9 fixtures for the rest of the squads.
   - Build two squads: one whose captain doesn't play, and one with Triple Captain or Bench Boost.
   - Hand-calculate both from raw events and lineups in `docs/rules/reference-week.md`, and write it so a second reviewer can reproduce it from the rulebook alone.
   - Add tests asserting the exact totals.
8. **Rule tests (OUR rules):**
   - captain 0 min, vice 6, nine others 30, −4 hit → 30 + 18 + 0 − 4 = **44**
   - captain plays only the 2nd fixture of a double week and scores 1, vice 6 → 30 + 3 + 12 − 4 = **41**

   Phatu's handbook reference uses FPL rules (2× captain, autosubs), giving 42 and 34. List that under "conflicts" with both calculations side by side, for sign-off.
9. **Pass when:**
   - every FPL difference is listed for product sign-off;
   - every scoring category has an attributable input, a worked example, a missing-data policy and a passing test;
   - the reference totals match;
   - past entries keep their rules version.

### SPRINT 3: Stage 3, account and team onboarding (`sprint/3-accounts`)
1. **Identity:**
   - The verified Supabase `sub` is the permanent key.
   - Stop requiring an email claim (Apple relay, phone and missing Facebook email must work); store email when present.
   - Keep the 409 recovery codes for legacy and conflicting emails.
   - Support Supabase's verified identity-linking flow, so a second provider maps to the same manager.
   - Document a recovery path for previously unlinked accounts.
   - Verify issuer, audience, signature and subject against the environment's Supabase project (the same project as both web and mobile clients).
2. **Username** (the public handle; it replaces display name):
   - 3–20 characters: letters, digits and underscore;
   - NFKC + case-fold to a canonical key with a unique index;
   - reserved names;
   - `PATCH /api/v1/me { username }` → `{"username":..., "onboarding":{...}}`;
   - a race → 409 `{"status":409,"code":"username_taken","title":"Choose another username"}`, without revealing the holder;
   - a rename cooldown setting and username history;
   - moderation, cooldown and recovery policies documented and visible through `GET /rules` or `/me`.
3. **Favourite club:**
   - `PATCH /api/v1/me { favouriteClubId }`, using football.teams.id with a current-season PSL fixture; null clears it.
   - `GET /api/v1/clubs` (public): id, sportmonks id, name, short code, crest (nullable).
   - Phatu asked for stable internal club IDs plus a mapping for existing web club selections. Add `docs/accounts/club-mapping.md` with this map (web slug → football.teams.id):

     | Web slug | teams.id |
     |---|---|
     | amazulu | 69 |
     | chippa | 83 |
     | durban_city | 96 |
     | golden_arrows | 81 |
     | chiefs | 76 |
     | sundowns | 28 |
     | marumo | 77 |
     | pirates | 50 |
     | polokwane | 62 |
     | richards_bay | 72 |
     | sekhukhune | 66 |
     | stellenbosch | 60 |
     | ts_galaxy | 65 |

     - Not in the web list: Kruger United 90, Milford 123, Siwelele 146.
     - No current club: supersport, capetowncity, magesi (they map to null).
     - Verify each id against the DB before writing the doc.
4. **Team name:**
   - 3–30 characters, trimmed, on create and rename; names may repeat.
   - `PATCH /api/v1/fantasy/team { name }`, owner only.
   - Keeps the team id, picks, memberships and ranks; records the name history.
5. **Onboarding state on `GET /me`:** `{ needsUsername, needsTeam, needsSquad }`. A fan can browse football without a team.
6. **Notification preferences:** goal alerts, deadline reminders and league updates, with GET and PATCH.
7. **Export and deletion:**
   - `GET /api/v1/me/export`.
   - `DELETE /api/v1/me` as **anonymisation** (no row deletes):
     - league ownership passes to the oldest member, or the league is archived if empty;
     - historical scores are kept, anonymised;
     - document the Supabase Auth steps.
   - Require recent authentication (token issued within N minutes) for deletion AND other sensitive changes.
8. **Suspension:** a suspended account flag. Suspended managers can read but not write, and are hidden from public leaderboards (document this).
9. **Leaderboards:** username and team name only.
10. **Provider setup:** write `docs/accounts/provider-checklist.md` (needs other owners):
    - separate staging and production credentials, callback allowlists and mobile return URLs;
    - Google OAuth clients;
    - Apple: relay address, first and repeat sign-in, rotation owner for the web OAuth secret;
    - Facebook: a non-developer user, and missing-email permission.
11. **Pass when (code side):**
    - renaming the username or team keeps both IDs and standings;
    - tests cover:
      - conflicting names;
      - missing email;
      - linking a second identity;
      - repeated first-access calls (idempotent profile creation);
      - expired tokens;
      - deleted accounts;
      - suspended accounts;
    - the provider checklist is ready for the staging round trip.

### SPRINT 4: Stage 4, deadline-safe writes (`sprint/4-safe-writes`)
1. **Target gameweek:**
   - Every squad, lineup, transfer and chip write names its target gameweek id.
   - Responses return server time, target gameweek, deadline and squad version.
   - An expired week → 409 with the current state. A stale request NEVER edits the next week.
2. **Atomic version check:** `UPDATE … WHERE version = @expected` in the same transaction as the write; 0 rows → 409. Handle concurrent FIRST saves too.
3. **Initial pick validation:** eligibility, price, budget, player count, club cap, positions, starters, bench order and captaincy.
4. **Autopick:** `POST /api/v1/fantasy/team/autopick/preview` returns a valid candidate or an explicit explanation; it doesn't save.
5. **Transfer preview:** `POST /api/v1/fantasy/team/transfers/preview` returns:
   - in/out players, sale proceeds and purchase cost;
   - bank before/after and free transfers before/after;
   - points hit and violations;
   - squadVersion and priceRevision.

   Shape it like Phatu's example quote in 03 Stage 6.
6. **Transfer confirm:**
   - Requires the quote versions, target gameweek and an `Idempotency-Key` header.
   - Recheck ownership, eligibility, quote, version and deadline at one decision point.
   - Atomically write transfers, picks, bank, allowance and version.
   - The key is bound to manager + operation + payload hash: same key + same payload → the original result; same key + a different payload → a clear error.
   - A price change → 409 with a new quote.
   - Returns a receipt.
7. **Error semantics** (documented, with machine-readable codes):
   - quote expired;
   - changed price;
   - insufficient funds;
   - stale squad;
   - locked gameweek;
   - unauthorised.

   Keep the existing statuses: 400 validation, 401, 403, 404 missing team, 409 conflict, 503 unavailable data.
8. **Transfer allowance ledger:** granted and used per gameweek; no rollover.
9. **Chips:**
   - per-season entitlements with a GET endpoint;
   - one chip per gameweek;
   - an activation and cancellation policy;
   - record how a chip affects previously charged hits (e.g. Wildcard after a hit was quoted).
   - No Free Hit under our rules; note Phatu's Free Hit restoration item as N/A, under decisions.
10. **Lock and carry-forward:**
    - At the deadline, persist an immutable entry: XI, bench order, captain, vice, chip, purchase prices and transfer costs.
    - Carry the squad forward.
    - Rivals' picks stay hidden until the deadline.
    - Add an operator report reconciling accepted receipts with locked entries at the deadline.
11. **Postgres tests:**
    - two devices editing one version;
    - a response lost after commit (the retry returns the original receipt);
    - a price change between preview and confirm;
    - a deadline straddle;
    - duplicate transfers → one charge;
    - the same key with a different payload → rejected;
    - a deadline coinciding with an API restart (the persisted state decides; the receipt is recoverable).
12. **Pass when:** the squad is always legal, each accepted action has one receipt, and the bank and hits balance.

### SPRINT 5: Stage 5, settlement, rankings and corrections (`sprint/5-settlement`)
1. **State machine:** open → locked → live → awaiting_review → final, stored with reason, input revision and time. Admin endpoints only allow valid transitions.
2. **Completeness (Phatu's "fix the current disagreement"):**
   - The API currently finalises on the worker flag OR all fixtures being final, even when data is incomplete. Replace this with a contract that reads `sync.gameweek_sync_status` plus anything defined in BACKEND_INTEGRATION.md.
   - Per-fixture outcomes:
     - verified zero (a quiet match can settle);
     - delayed feed (stays under review);
     - awarded, abandoned, postponed;
     - manual resolution (recorded, authorised).
   - A finished-but-incomplete fixture keeps the week in awaiting_review.
   - Late facts after final → an amendment revision.
   - Missing worker evidence goes under "needs sync team".
3. **Scoring:**
   - per fixture, then aggregate;
   - on-pitch intervals for minutes and clean sheets;
   - preserve the inputs for assists, cards, subs, penalties and reversals;
   - captain fallback after ALL of the captain's fixtures in the week (no autosubs);
   - record the multiplier and fallback reasons.
4. **Revisions:**
   - provisional, final and amendment revisions, each with a breakdown (player events, multipliers, bench contribution, transfer deductions);
   - calculated in batches and published atomically;
   - an interrupted run resumes or replays without double-counting.
5. **Rankings:**
   - from the same revision as the points page;
   - tie, late-join and eligibility rules;
   - paginated and cached by revision + page;
   - an efficient "my rank" (no loading season-wide totals for every manager's leagues);
   - rank movement and historical results.
6. **Explain endpoint:** `GET /api/v1/fantasy/team/points/explain?gameweek=N`.
7. **Corrections:**
   - admin preview of the impact (players, teams, ranks);
   - then publish an amendment recording source, author, reason and previous revision;
   - flag a correction message.
8. **Operations ownership (02):** write `docs/ops/fantasy-operations.md`, saying who or what triggers each job and how:
   - creating gameweeks (admin route, synced fixtures, editable deadline);
   - finalising;
   - rescoring;
   - applying reviewed transfer-window prices.
9. **Tests:**
   - repeat settlement → identical scores and ranks;
   - a corrected assist changes only the affected totals;
   - a rescinded goal or red card is removed, with original + correcting revisions in history;
   - a sparse genuine 0–0 is told apart from missing data;
   - a defender off at 60 before a later concession → clean-sheet rule applied using pitch time;
   - an interrupted run → a clean resume;
   - a non-admin cannot score, finalise or price (403);
   - Sprint 2's reference totals still match.
10. **Pass when:** totals match by player, manager and league; reruns are stable; the explain endpoint can answer "why did my score drop?".

### SPRINT 6: Stage 6, a supported frontend API (`sprint/6-frontend-contract`)
1. **OpenAPI:**
   - Export `docs/api/openapi-v1.json` from the build, with examples for every endpoint and error.
   - CI fails if the file is out of date.
   - Contract tests check the actual response envelopes against it.
2. **Web compatibility (02):**
   - Fixtures use `kickoffUtc`. The web currently expects `kickoff`: document the migration, and serve both during rollout if needed.
   - Retire synthetic player ratings, or label a separately defined rating.
   - Port `/standings`, `/player-rankings` and `/matches?fixtureId=` from the Netlify functions, keeping the competition and season filters, ranking tie rules, events, stats and lineups.
3. **Player detail:**
   - stable id, club, position, eligibility, recent minutes, availability source, upcoming fixtures, points history and points reasons;
   - duplicate names resolved by id;
   - a club move changes future eligibility, while history keeps the original club.
4. **Club detail.** Paging on all lists.
5. **Leagues:**
   - pagination, rank history and visibility rules;
   - ownership transfer and archive (no row deletes).
   - Tests:
     - tied ranks;
     - concurrent joins at the 20 cap (one wins, clear responses for both);
     - repeated join requests;
     - owner departure policy;
     - a public rank vs an unpublished squad;
     - one membership per team.
6. **CORS:** expose ETag for allowed origins, which come from per-environment config.
7. **Deployment (02):**
   - a local launch target and staging/production configuration for the .NET 10 service: scoped DB role, Supabase settings, explicit origins;
   - separate health checks for DB readiness and data freshness (minimal public output).
8. **Shared-team end-to-end test (02 release gate):**
   - admin creates a gameweek → GET /me → POST team → PUT 15 picks → GET snapshot as a "second client";
   - both reads show the same team id, picks and balance (R100m budget; Phatu's doc says R300m, which was scrapped, so list that under conflicts);
   - a stale edit gets a recoverable 409.
9. **Staging kit:**
   - admin-protected and staging-only;
   - two reference managers, a resettable private league (reset = archive + recreate), stable fixture IDs and a staging account;
   - documented in `docs/api/staging-kit.md`.
10. **News:**
    - Propose one article and category contract through C#, with `/news/headlines?max=` compatibility during rollout.
    - Preserve article ids, attribution, filters and paging.
    - It goes under "needs other owners" for the news-service owner; follow `05-news-service.md` if it's in docs/plans.
11. **Delivery proposals:** write `docs/api/delivery-proposal.md` (decisions for the team):
    - register/retire devices;
    - follows (club/player/match) and preferences;
    - the last synced state for reconnection;
    - goal alerts read committed Postgres facts, store deduplication state, and send a correction event when a goal is rescinded;
    - following both a club and its fixture still gives one alert per device.
12. **Pass when:** an end-to-end test using docs/api only:
    - follows a fixture;
    - onboards two managers and puts their squads in one league;
    - makes a transfer, views points and explains a rank change;
    - with no manual DB edits.

### SPRINT 7: Stage 7, operate and secure (`sprint/7-operate-secure`)
1. **CI:**
   - `dotnet restore Touchline.sln`
   - `dotnet build Touchline.sln --no-restore --configuration Release`
   - `TOUCHLINE_REQUIRE_DB_TESTS=true dotnet test Touchline.sln --no-build --configuration Release`

   Record the result against the API revision. Add:
   - Postgres cases for concurrent squad mutations, deadline freezes, settlement interruption and duplicate retries;
   - a full gameweek replay test that checks the public API result;
   - dependency vulnerability checks;
   - secret scanning.
2. **Load test:**
   - a k6 or NBomber script covering the most expensive standings request plus deadline writes, cache misses and concurrent goal updates;
   - targets from the handbook:
     - match reads p95 < 500 ms (warm and cold cache reported separately);
     - squad confirmations p95 < 1 s with zero lost or duplicate writes and a ledger reconciliation;
     - live update latency p95 ≤ 30 s, reported for what can actually be observed;
   - documented; never run against production.
3. **Identity:**
   - pinned issuer and audience, signature and expiry;
   - tests for expired tokens, JWKS key rotation and suspension.
4. **Access matrix:**
   - write `docs/security/access-matrix.md`: every endpoint × role (anonymous fan, manager, league owner, support, operator, worker, admin);
   - prove that ASP.NET checks ownership and membership itself, because the API role's RLS is broad;
   - cross-user tests;
   - a SQL script to check the effective permissions of the direct Supabase roles (anon, authenticated, app_api_user, the worker role). Its output goes in the report.
   - Manual sync, scoring, pricing and calendar changes are restricted to their roles.
5. **Config and secrets:**
   - production startup rejects incomplete config;
   - production and staging kept separate;
   - no secrets in code or logs;
   - a secret-rotation runbook.
6. **Data exposure:**
   - public match reads cached separately from authenticated ones;
   - tests that squads, invite codes, emails and device tokens never appear in public or cached responses;
   - a minimal public health endpoint; diagnostics are admin-only or in restricted logs.
7. **Rate and size limits:** sign-in-related calls, renames, league joins, search, transfers and standings reads.
8. **Audit log:** actor, time, reason and outcome for scoring, pricing, calendar, corrections, admin resets and manual sync.
9. **Privacy:**
   - write `docs/security/privacy-inventory.md` (data, purpose, retention, location, recipients);
   - document how backups and historical standings handle deletion;
   - link it to Sprint 3's export, correction, deletion and opt-out flows;
   - flag it for a POPIA review.
10. **Runbooks:**
    - `docs/ops/release-runbook.md`: restore, rollback, credential rotation, incident escalation, and switches to pause scoring or alerts independently.
    - `docs/ops/matchday-runbook.md`, from the handbook's "Matchday operations": before first kickoff, during play and settlement, and the signal → action → evidence table.
11. **Release record:** `docs/release/release-record.md`, copied from the handbook's Gates 1–9 table and checklist and pre-filled with the evidence links produced by Sprints 1–7.
12. **Pass when (code side):**
    - CI runs every DB test, the dependency check and secret scanning;
    - the access-matrix tests pass;
    - startup rejects bad production config;
    - the load script, runbooks and release record exist.

    Human items go under "needs other owners".

---

## Part 3: Agreed Touchline rules (never change these; propose under "decisions for the team")

**Squad and lineup**
- Budget R100.0m, stored in millions. Phatu's doc mentions R300m; that's scrapped.
- Squad of 15: 2 GK / 5 DEF / 5 MID / 3 ATT, max 3 per club.
- Starting XI: exactly 1 GK, in 4-3-3, 4-4-2, 3-5-2, 3-4-3 or 5-3-2 (min 3 DEF).
- Bench: 1 GK + 3 outfield. The bench GK only swaps with the GK.
- Only players in a current-season lineup are pickable (admin override available).

**Deadline and transfers**
- Deadline: 90 min before the gameweek's first kickoff.
- Transfers are like for like.
- 1 free transfer a week, no rollover, −4 per extra.
- Unlimited transfers before the GW1 deadline and with Wildcard.
- Players sell at their current price.

**Captaincy**
- Captain ×3, vice ×2.
- The vice gets ×3 only if the captain plays 0 minutes across ALL of the gameweek's fixtures.
- A missing captain or vice is filled by a deterministic pick at scoring.

**Chips**
- Triple Captain: ×4, passes to the vice if the captain plays 0.
- Bench Boost and Wildcard.
- Each chip once per season, one per gameweek. No Free Hit.

**No auto-subs.** A starter who didn't play scores 0.

**Scoring**

| Event | Points |
|---|---|
| Appearance, 45+ min | +1 |
| Appearance, full match | +2 |
| Goal | +3 |
| Assist | +2 |
| Clean sheet (GK/DEF, 60+ min) | +2 |
| Yellow card | −1 |
| Red card | −2 |
| Second yellow | −2 total |
| Own goal | −2 |

**Minutes**
- Counting is inclusive: subbed off at minute m plays m; subbed on at minute m plays length − m + 1.
- Stoppage time: 45+x counts as 45 and 90+x counts as 90. Minutes are capped at the match length.

**Live and offline**
- Live provisional points.
- Offline support: snapshot, ETag/304, If-Match → 409.

**Leagues**
- Private leagues join by code only.
- Code format: TL- plus 6 digits, with # ! @ - between them.
- Max 20 members. The owner can't leave.
- Plus an Overall league.

**Pricing:** per transfer window using the agreed formula, which is already implemented. Don't change it.

---

## Part 4: Human launch checklist (from Phatu's docs; Claude Code lists these in FINAL-SUMMARY, it does NOT do them)

**Leruo / Phatu**
- [ ] Sign off the rulebook and every FPL difference, including the captain/autosub conflict: our 44/41 vs the handbook's 42/34.
- [ ] Resolve the "conflicts" and "decisions for the team" lists.
- [ ] Second reviewer reproduces the two reference-team totals.

**Sync team**
- [ ] Fix the stopped event sync (since 1 Nov).
- [ ] Backfill 2025/26 lineups.
- [ ] Populate `sync.gameweek_sync_status`.
- [ ] Record the evidence Sprint 5 lists.
- [ ] Identify the active scheduler.

**Mobile / Supabase**
- [ ] Real staging callbacks for Google, Apple and Facebook, including linking, recovery and a second device. The mobile owner signs off the round trip.
- [ ] MFA for production admins.

**Ops**
- [ ] Rotate all leaked keys.
- [ ] Timed restore in an isolated environment.
- [ ] Deadline load test in staging.
- [ ] Production-role access tests.
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
