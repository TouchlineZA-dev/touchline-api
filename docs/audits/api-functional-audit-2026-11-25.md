# Functional audit of touchline-api, 25 November 2026

**Question:** can a manager play the whole game through the API, with no dead ends and without being logged out, with every rule and limit enforced? And does the API support step-by-step wizards with drafts wherever the manager gives input?

**Source of truth:** `docs/rules/rulebook-2026-27-v1.md` and the agreed rules (RUNBOOK Part 3). No agreed rule was changed.

**How the audit was run**
- **Write scenarios:** run against a disposable PostgreSQL 17. It was built from the pinned Flyway migrations: touchline-sync `main` at `c817044` (V001–V050), plus this branch's V051.
- **Seeded world** (`tests/Touchline.Api.Tests/Audit/AuditWorld.cs`):
  - a current PSL season;
  - 16 clubs, each with 15 priced players (2 GK, 5 DEF, 5 MID, 3 ATT) who all appeared in a current-season lineup;
  - 4 unsigned players with no lineup;
  - four gameweeks: one locked, one open, two later;
  - fixtures and events.
- **Live checks:** read-only. SQL ran as `app_api_user` inside `BEGIN READ ONLY`, and the HTTP calls to Railway were GETs only.
- **Never done:** no password or token was asked for, printed or stored, and nothing was written to the shared database or to Railway.

**Rerun:** every scenario is a test in `tests/Touchline.Api.Tests/Audit/`.

```sh
TOUCHLINE_TEST_PG="Host=localhost;Port=55432;Username=postgres;Password=..." TOUCHLINE_REQUIRE_DB_TESTS=true \
  dotnet test Touchline.sln --filter "FullyQualifiedName~Touchline.Api.Tests.Audit."
```

## Results

**85 audit test cases: 85 passed, 0 failed.** The full suite is **504 passed, 0 failed, 0 skipped**.

| Area | Test cases | Pass | Fail |
|---|---|---|---|
| 1 Account and onboarding | 17 | 17 | 0 |
| 2 Squad building | 17 | 17 | 0 |
| 3 Substitutions | 4 | 4 | 0 |
| 4 Transfers | 8 | 8 | 0 |
| 5 Chips | 3 | 3 | 0 |
| 6 Deadlines and locking | 4 | 4 | 0 |
| 7 Scoring | 11 | 11 | 0 |
| 8 Leagues | 6 | 6 | 0 |
| 9 Security and "doesn't throw you out" | 4 | 4 | 0 |
| 10 Full journey | 1 | 1 | 0 |
| Wizard step validation (Part 2) | 5 | 5 | 0 |
| Drafts (Part 2) | 5 | 5 | 0 |

**A note on the "Actual" column:** the tables below show results after the fixes on this branch. Seven of the rules failed before those fixes; each one is listed under "Bugs found".

### 1. Account and onboarding

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| First `/me` creates the profile; flags update after each step | needsUsername → needsTeam → needsSquad → done; one profile | As expected; `step` follows: username, team, squad, done | `AccountAuditTests.A01_…` | PASS |
| Username 3–20 characters, letters, digits and underscore | Others rejected with a named rule | `username_length`, `username_characters` | `A02_…`, `A03_…` | PASS |
| Reserved names blocked | `admin`, `touchline…` refused | `username_reserved` | `A02_…` | PASS |
| NFKC and case-insensitive uniqueness; a clash is 409 | Upper case and full-width copies clash | 409 `username_taken`; never says who | `A04_…` | PASS |
| Cooldown after choosing a username | A second change within 30 days is refused | 400 `username_cooldown` (was generic `rule_violation`) | `A05_…` | PASS |
| Favourite club: set, clear, current clubs only | Set and clear work; an unknown club is refused | `favourite_club_unknown` | `A06_…` | PASS |
| Team name 3–30 characters on create and rename | 2 and 31 are refused, 3 and 30 accepted; trimmed | `team_name_length` | `A07_…` (5 cases) | PASS |
| Rename keeps the team id and records history | Same id; a history row | As expected; a second team is 409 `team_exists` (was code-less) | `A08_…` | PASS |

### 2. Squad building

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| A legal 15 saves; bank = R100.0m minus its cost | 200; bank R33.0m for the R67.0m squad | As expected | `SquadAuditTests.S01_…` | PASS |
| Budget R100.0m | Exactly R100.0m allowed; R100.5m refused | 200 with bank R0.0m; 400 `over_budget` | `S02_…` | PASS |
| Exactly 2 GK, 5 DEF, 5 MID, 3 ATT | Other counts refused | `position_count` | `S03_…` | PASS |
| At most 3 from one club | 3 fine; the 4th refused | `club_limit` | `S04_…` | PASS |
| No duplicates; ineligible players refused; admin override | Duplicates and no-lineup players refused; after an override, accepted | `duplicate_player`, `player_unavailable`; accepted after override | `S05_…` | PASS |
| XI formations 4-3-3, 4-4-2, 3-5-2, 3-4-3, 5-3-2 only; at least 3 DEF | All five save; 2-5-3, 4-5-1, 4-6-0 refused | `formation_range` / `formation_not_allowed` | `S06_…` (3), `S07_…` (5) | PASS |
| Exactly 1 GK in the XI; bench 1 GK + 3 outfield | Two starting goalkeepers refused | `starting_gk`, `bench_gk` | `S08_…` | PASS |
| Captain and vice are different starters | Same player, benched or missing armbands refused | `captain_vice_same`, `captain_not_starter`, `vice_captain_not_starter`, `captain_missing` | `S09_…` | PASS |
| First pick only once | A second pick is 409 | `squad_already_picked` | `S10_…` | PASS |
| Autopick: legal squad within budget, or an explanation; never saves | Valid 15 ≤ R100m that saves as given; bad formation explained; onboarding unchanged | As expected | `S11_…` | PASS |

### 3. Substitutions (`PUT /fantasy/team/lineup`)

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| Bench↔XI swaps, including a formation change, save; repeated swaps up to the deadline | 200 each time | As expected | `LineupAndDeadlineAuditTests.L01_…` | PASS |
| Swaps that break the formation are refused | 2 DEF left: refused | `formation_range` | `L02_…` | PASS |
| The bench GK only swaps with the GK | GK into midfield refused; GK↔GK allowed | `bench_gk_swap`; GK↔GK 200 | `L02_…` | PASS |
| Lineup can't add players | A player from outside the squad is refused | `lineup_changes_players` | `L02_…` | PASS |
| Bench order saved; captaincy revalidated | Order round-trips; a benched captain is refused | As expected; `captain_not_starter` | `L03_…` | PASS |
| After the deadline: 409 and nothing changes | 409; locked and next week unchanged | 409 `gameweek_locked` with `current.nextGameweek`; both weeks unchanged | `L04_…` | PASS |

### 4. Transfers

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| 1 free per gameweek, −4 each extra, no rollover | 1 free; 3 transfers = −8; a week without transfers still gives 1 | As expected | `TransferAndChipAuditTests.T01_…` | PASS |
| Unlimited and free in the first week (late entry), before the GW1 deadline, and with Wildcard | `freeTransfersBefore: null`, hit 0 | As expected | `T02_…`, `T08_…` | PASS |
| Like-for-like only; club limit and bank rechecked | DEF→MID, a 4th from a club, overspending all refused | Quote `valid: false`; confirm 400 `rule_violation` / `insufficient_funds` | `T03_…` | PASS |
| Sell and buy at the current price; bank balances; quote = result | Sale at the new price; receipt and team bank equal the quote | As expected | `T04_…` | PASS |
| Price change between preview and confirm | 409 with a new quote; nothing saved | 409 `price_changed`; `current` is the new quote | `T05_…` | PASS |
| Idempotency-Key: same key + same body | The original receipt | `replayed: true`, same receipt id | `T06_…` | PASS |
| Same key + different body | Refused | 409 `idempotency_key_reused` | `T06_…` | PASS |
| Duplicate requests at the same moment | One charge, the same receipt for both | One charge; both get the receipt (**fixed**: the second used to get 409 `stale_squad`) | `T06_…` | PASS |
| Stale squad version | 409 with the current state | 409 `stale_squad` with `current` (legacy If-Match and confirm) | `T07_…` | PASS |

### 5. Chips

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| One chip per gameweek; Bench Boost and Triple Captain cancellable before the deadline | Second chip refused; cancel then another works | `chip_one_per_gameweek`; cancel 200 | `C01_…` | PASS |
| No Free Hit | Refused | `chip_unknown` | `C01_…` | PASS |
| Each chip once per season; Wildcard can't be cancelled; no cancelling after the deadline | Refused | `chip_used`, `chip_not_cancellable`, `gameweek_locked` | `C02_…` | PASS |
| Triple Captain ×4, passes to the vice (×4) if the captain plays 0; Bench Boost counts the bench | Totals as the rulebook | As expected | `C03_…` | PASS |

### 6. Deadlines and locking

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| Deadline = 90 minutes before the first kickoff | Shown on the public calendar | As expected (**fixed**: `/gameweeks` had no deadline) | `D01_…` | PASS |
| Writes after the deadline never edit the next week | 409; no chip and no transfer recorded | 409 `gameweek_locked`; nothing written | `D02_…`, `L04_…` | PASS |
| Rivals can't see picks before the deadline | No route exposes another team's players | League standings carry names and points only | `D03_…` | PASS |
| Late entry: next open week, unlimited transfers, no back points | First squad goes to the open week; no entry for the earlier week | As expected | `D04_…` | PASS |

### 7. Scoring

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| Appearance 1 (45+), 2 (full match) | 45 → 1, 44 → 0, 90 → 2 | As expected | `ScoringAuditTests.P01_…` | PASS |
| Goal +3, penalty goal (16) +3, assist +2, own goal (15) −2 | As listed | As expected | `P02_…` | PASS |
| Clean sheet +2 for GK and DEF with 60+ minutes | 60 yes, 59 no; never MID or ATT; not after conceding | As expected | `P03_…` | PASS |
| Yellow −1, red −2, second yellow −2 in total | As listed; a red at m plays m | As expected | `P04_…` | PASS |
| Minutes: off at m = m; on at m = length − m + 1; 45+x → 45; 90+x → 90; capped | 46/45; stoppage clamps; never over 90 | As expected | `P05_…` | PASS |
| Captain ×3; vice ×2, or ×3 only if the captain plays 0 all week (double weeks too) | 44 and 41 | 44 and 41 | `P06_…` | PASS |
| No automatic substitutions | A non-playing starter scores 0; the bench doesn't count | As expected | `P07_…` | PASS |
| No captain → deterministic pick | Same pick whatever the order | As expected | `P08_…` | PASS |
| GW9 reference totals; identical on re-run | 65 and 61; same breakdowns twice | As expected | `ScoringReferenceAuditTests.P09_…` (2) | PASS |
| Withdrawn events don't score | A goal with `withdrawn_at` gives 0 | As expected | `WithdrawnEventAuditTests.P10_…` | PASS |

### 8. Leagues

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| Code `TL-` + 6 digits with 2 of `# ! @ -` between digits; join by code only | Shape always holds; malformed refused; no league list | `league_code_format`; unknown code 404 | `LeagueAuditTests.G01_…` | PASS |
| At most 20; the 21st refused, also with two joins at once for the last place | Exactly one of two racers joins; 21st refused | 409 `league_full` (**fixed**: had no code); 20 members | `G02_…` | PASS |
| Owner can't leave; leave and rejoin work; Overall exists | As listed | `owner_cannot_leave`; `already_member` (**fixed**: had no code) | `G03_…` | PASS |
| Private standings for members only | Outsider refused | 403 `forbidden` | `G04_…` | PASS |
| Tie-breaks | Points, overall points, username (A–Z, ignoring case), team id | As expected | `G05_…` | PASS |
| Ranks use the same scoring as the points page | Week and overall rows equal `/points` totals; both provisional | As expected (`revision` ends `+live`) | `G06_…` | PASS |

### 9. Security and "doesn't throw you out"

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| Every signed-in route (all of them, from the route table) rejects no, invalid and expired tokens | 401 each, with a reason | `token_missing`, `token_invalid`, `token_expired`, plus `retry` and RFC 6750 header (**fixed**: 401 had no body) | `SecurityAuditTests.X01_…` | PASS |
| An expired token is different from missing or invalid | Machine-readable, so the app refreshes | `code: "token_expired"`, `retry: "refresh"`, `WWW-Authenticate: Bearer error="invalid_token", error_description="The access token expired"` | `X01_…` | PASS |
| 400, 403 and 409 never look like a sign-out | Never 401, no `WWW-Authenticate`, always a `code` | As expected (**fixed**: unreadable bodies now `invalid_request`) | `X02_…`, `T06_…` | PASS |
| Non-admins get 403 on admin routes | 403 `forbidden` | As expected | `X03_…` | PASS |
| A can't read or edit B's team, squad, transfers, drafts or private league | Every route is scoped to the token's user | As expected | `X04_…` | PASS |

**What a 401 means for the app** (also in `docs/api/errors.md` and `docs/ui/wizard-contract.md`):
- **Refresh and retry once:** 401 `token_expired` (`retry: "refresh"`).
- **Sign in again:** 401 `token_missing` or `token_invalid` (`retry: "sign_in"`), and 403 `account_deleted`.
- **Everything else** (400, 403, 404, 409, 413, 429, 5xx) is about the request or the data: keep the form and show the message.

### 10. Full journey

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| New user → `/me` → username → favourite club (skipped) → team → squad (wizard checks and drafts) → lineup and captain → transfer → chip → league create and join → points, explanation, snapshot | Each step succeeds or returns a code the UI can show; no database fixes | As expected | `JourneyAuditTests.J01_…` | PASS |

### Wizard support (Part 2)

| Rule | Expected | Actual | Test | Result |
|---|---|---|---|---|
| Partial squad: live progress, never saves | "4/5 DEF, R67.0m left", next steps, no violations | As expected; works before a team exists | `WizardAndDraftTests.W01_…` | PASS |
| Broken rules named as soon as they happen | `club_limit`, `position_count`, `duplicate_player`, `player_unavailable`, `over_budget` | As expected | `W02_…` | PASS |
| A complete check matches the save | Same rules as `PUT /squad`; formation, bench and captaincy reported | As expected | `W03_…` | PASS |
| Lineup check; never saves | `bench_gk_swap`; version unchanged; partial lineup shows next steps | As expected | `W04_…` | PASS |
| Transfer check: unfinished and complete sets | "Buy 1 defender", bank, cost and hit | As expected | `W05_…` | PASS |
| Draft save, load and overwrite; stale save is 409 | Second device wins; stale and "fresh over live" are refused; one of two racing saves wins | 409 `draft_stale` with `current` | `R01_…` | PASS |
| Clear is an UPDATE; no DELETE grant | Row kept with `cleared_at`, payload emptied; can start again | As expected | `R02_…` | PASS |
| Expiry; kind, shape and size checked | Expired = gone; bad kind or shape 400; over 64 KB 413 | As expected | `R03_…` | PASS |
| Drafts are never a squad and never scored | No picks, no entries, bank unchanged; `/me` shows the draft | As expected | `R04_…` | PASS |
| Before migration 015: drafts 503, everything else works | 503 `drafts_unavailable`; `/me`, team and checks work | As expected | `R05_…` | PASS |

## Bugs found

Each one was fixed on this branch with a test. None of them changed an agreed rule.

1. **An expired token got a bare 401 with no body.** The app couldn't tell "refresh and retry" from "sign in again", so a lapsed session looked like a logout.
   - **Fix:** every 401 from the token check now carries `code` (`token_missing`, `token_expired` or `token_invalid`), a `retry` hint (`refresh` or `sign_in`) and the RFC 6750 header (`Users/AuthChallenge.cs`).
   - **Test:** `X01`.
2. **Duplicate transfer confirmations sent at the same moment.** Both had the same Idempotency-Key and were charged once, but the second got 409 `stale_squad` instead of the original receipt. A double tap showed an error even though the transfer went through.
   - **Fix:** after taking the team lock, the confirm checks again for a stored receipt with that key (`Services/SafeWriteService.cs`).
   - **Test:** `T06`.
3. **Unreadable request bodies came back in ASP.NET's own shape.** They had no `code`, and `errors` was a dictionary, unlike every other 400.
   - **Fix:** they are now 400 `invalid_request`, with `errors` as a list and `violations` naming each field (`Controllers/InvalidRequest.cs`).
   - **Test:** `T06`.
4. **Some 409s had no `code`:** a second team, a full league, and joining a league twice.
   - **Fix:** they now use `team_exists`, `league_full` and `already_member`. Any other 409 without a specific code gets `conflict`.
   - **Tests:** `A08`, `G02`, `G03`.
5. **Rule violations were English text only.** A wizard couldn't tell which field to highlight.
   - **Fix:** every 400 now has `violations: [{ rule, message }]` next to the unchanged `errors`. The rule names are listed in `docs/api/errors.md`.
   - **Tests:** all `S`, `L` and `C` tests.
6. **The username cooldown used the generic `rule_violation` code.**
   - **Fix:** it is now `username_cooldown`.
   - **Test:** `A05`.
7. **The public calendar `/gameweeks` had no deadline.** The app couldn't show "pick your team by …" before sign-in.
   - **Fix:** it now returns `deadline` and `deadlineSast`, using the same rule as the fantasy routes (stored, or 90 minutes before the first kickoff).
   - **Test:** `D01`.

**Also changed**
- **New rate limits:** wizard autosave would have hit the 10-per-minute "account" limit. Drafts and step checks have their own limits: 60 and 120 a minute per manager.
- **Test schema re-pinned:** `tests/db/flyway` now matches touchline-sync `main` at `c817044`. The test harness accepts V001's new password-free `CREATE USER` lines.

**Still failing:** nothing.

## Real-data findings (read-only, 25 November 2026)

These come from live GETs to Railway and read-only SQL as `app_api_user`. Season 15 (2026/2027, PSL) is the current one.

| Finding | Detail | Effect on real managers |
|---|---|---|
| **No prices for 2026/27** | `app.player_prices` has 0 rows for season 15; there are no price changes and no transfer windows | Every player falls back to his position floor: GK R4.0m, DEF R4.0m, MID R4.5m, ATT R4.5m |
| **Every legal squad costs exactly R64.0m** | Cheapest and most expensive legal squads are both R64.0m | The R100m budget, "budget left", over-budget errors and price strategy never matter. A real squad is always legal on money |
| Pickable players | 478 of 2,100 are available (current-season lineups): GK 48, DEF 148, MID 135, ATT 147. All 16 clubs have at least 2 GK, 7 DEF, 7 MID and 4 ATT | A legal 15 can always be built, with plenty of choice |
| Unknown positions | 8 players, none of them pickable | No effect |
| Unavailable players with a club | 2 | Shown but not pickable; expected for players with no lineup this season |
| Existing teams with an old bank | 4 teams in season 15; banks range from R36.0m to **R300.0m** (2 teams above R100m) | Teams created under the old R300m default keep an unfair bank |
| Football data stopped | Last finished fixture 31 Oct; 6 fixtures past kickoff still "Scheduled"; freshness 178 h | Points and live views are stale (known: no PSL data since 18 Nov) |
| Drafts table | Not in the live database (migration 015 / V051 not applied) | Draft routes answer 503 `drafts_unavailable` until applied; everything else works |
| Seasons flagged current | 10, one per competition (PSL is season 15, Sportmonks 806) | No effect: the API picks the PSL one |

**Health check** (`scripts/api-health-check.ps1 -NoAuth`, from PR #15, against Railway):

```
UP    Public     Health                          200    857ms  status=ok
UP    Public     Health: ready                   200    396ms  ready=True
WARN  Public     Health: freshness               200    742ms  fresh=False, last football data 177.9 h ago, stalled live fixtures=2 (sync not delivering)
UP    Public     Current season                  200    398ms  2026/2027 (id 15)
UP    Public     Teams                           200    478ms  16 clubs
UP    Public     Clubs                           200    491ms  16 clubs
UP    Public     Club detail                     200   2164ms  club 69
UP    Public     Players                         200   1362ms  2100 players
UP    Public     Player detail                   200   1853ms  player 13146
UP    Public     Player rankings                 200   1269ms
WARN  Public     Fixtures                        200    582ms  240 fixtures; 6 past kickoff but still 'Scheduled' (sync not updating)
UP    Public     Match detail                    200   1303ms  fixture 1389, stale=False, revision r-9d0cb82ab9148d6b (api_content_hash)
UP    Public     Matches (web compat)            200   1185ms  fixture 1389
UP    Public     Gameweeks                       200    467ms  28 gameweeks
UP    Public     Standings                       200    725ms  16 rows
UP    Public     Rules                           200    342ms  version 2026-27.v1
UP    Security   /me without token -> 401        401    330ms  Rejects anonymous callers
UP    Security   /fantasy/team no token -> 401   401    293ms  Rejects anonymous callers
UP    Security   /admin/diag no token -> 401     401    281ms  Rejects anonymous callers
SKIP  Signed-in  Signed-in and admin checks                    -NoAuth

Summary: 17 up, 2 warn, 0 down, 1 skipped
```

## Decisions for the team

1. **Run the 2026/27 price run before launch.** An admin runs `POST /admin/prices/preview`, then `/admin/prices/recalculate` with a transfer window. Without it, every squad costs R64.0m and the budget is meaningless. This is an operational step, not a rule change.
2. **What to do with existing banks above R100m** (R300.0m from the old default). Options:
   - reset the banks: a reviewed data fix, not done here;
   - delete and recreate the test teams;
   - leave them, if they are test accounts.
3. **429 before 401.** Rate limits are checked before the token, so a busy client with an expired token can see 429 first. It recovers on its own (wait, retry, then refresh), so the API keeps this order unless the team prefers otherwise.
4. **Draft expiry.** The default is 14 days (`Drafts:ExpiryDays`). Confirm, or choose another value.
5. **Draft size.** 64 KB per draft, which is the same as the API's request-size guard. Confirm this is enough for the UI's payloads.
6. **Onboarding order.** `step` is username, then team, then squad. Favourite club is optional and never blocks. Confirm this matches the screens.
