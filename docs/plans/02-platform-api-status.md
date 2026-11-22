# Platform API plan: status (2026-11-23)

Status of Phatu's plan `02-platform-api.md` against touchline-api `main` at `053f047`. The plan isn't in this repo yet; it was written against his local `phathu-dev` at `9fbb5bc`, which has since been merged into `main` (PR #2), so parts of it are already done.

**Sources of evidence:** code and tests on `main`; the fantasy audit on branch `audit/fantasy-2026-11-23` (`docs/audit/FANTASY_AUDIT_2026-11-23.md`, PR open, not yet merged); touchline-sync branch `flyway-catchup` (V033–V038, not yet merged).

**Decisions that settle parts of the plan** (not conflicts):

- Budget stays **R100.0m** with `Pricing:PriceScale` 1.0. The plan's R300m doesn't apply.
- Profile features are **in**: display name, favourite club, team rename (proposal in [section 3](#3-profile-features-proposal)).

Status: **done** · **partly** · **open**. "Needs sync team" marks work that needs touchline-sync code, not just schema. Schema changes go in both touchline-sync `db/migrations` (next Flyway version, safe to rerun) and touchline-api `docs/migrations`.

## 1. Status

### Foundation

| Plan item | Status | Evidence | What's left |
|---|---|---|---|
| Keep the dedicated API as the foundation; deploy this repo | done | `src/Touchline.Api` on `main`: football reads, users, teams, squads, transfers, chips, leagues, points, admin | Deployment itself (see 7) |
| Promote `phathu-dev` auth work through CI | done | PR #2 merged (`d702b86`); `tests/Touchline.Api.Tests/Auth/*`; `.github/workflows/ci.yml` sets `TOUCHLINE_REQUIRE_DB_TESTS=true` | — |
| Keep deployment settings in the environment | done | `.env.example`, `DotEnv.cs` (Development only), env vars in hosting; no secrets in tracked files (checked 2026-11-22) | — |

### First end-to-end flow

| Plan item | Status | Evidence | What's left |
|---|---|---|---|
| 1a. Fixture builds from the canonical migration, not the deleted V033 | done | `PostgresFixture.cs` reads `docs/migrations/002_users_auth_link.sql` (merge `fbb6d8f`) | — |
| 1b. Auth and fantasy persistence tests on disposable Postgres with the real grants | partly | 14 Postgres `/me` tests (Testcontainers, CI only). They run as the container admin, with only `app.users` and `app.fantasy_teams` | Build the full schema from `docs/migrations` 001–006; connect as a role with `app_api_user`'s grants; add fantasy persistence tests (PR 1) |
| 1c. Exercise a populated upgrade and a clean install | partly | touchline-sync V033–V038 made safe to rerun on `flyway-catchup` (`cfded8f`); docs/migrations 001–006 mirror them (`bfad364`) | Test both paths on disposable Postgres; merge `flyway-catchup`; Flyway baseline/repair on the shared DB (needs sync team) |
| 2a. One Supabase project per environment; verify issuer, audience, signature, subject | done | `SupabaseAuthSetup.cs`; `Auth/TokenValidationTests` (wrong issuer, audience, key, alg none, tampered, expired) | Per-environment config (PR 7) |
| 2b. Keep the 409 recovery for legacy and conflicting emails | done | `UserService.ThrowIfEmailTakenAsync`; codes `legacy_email_unlinked` / `email_in_use`; `MeEndpointTests`; fantasy routes return the same `code` (`fbb6d8f`) | — |
| 2c. Profile update: display name, favourite club | open | — | PR 4 (spec in section 3) |
| 2d. Team rename | open | `fantasy_teams.name` exists; no rename endpoint | PR 4 |
| 2e. Stable internal club IDs; map existing club selections | open | Proposal in section 3 | PR 4 |
| 3a. Create the season's gameweeks from synced fixtures, with an editable deadline | partly | 28 gameweeks seeded by `docs/migrations/004` (applied); `POST /admin/gameweeks` creates one with a deadline | No endpoint to edit an existing gameweek's deadline (PR 8) |
| 3b. Sign in → /me → create team → 15 picks → snapshot | done | Audit live test with two users (audit report §4): /me, POST team 201, PUT squad 200, snapshot 200 | — |
| 3c. Check the returned balance (plan says R300m) | done | Budget R100.0m (decision); live: R64.0m squad → R36.0m left; `Budget_after_a_first_pick_is_the_budget_minus_current_prices` | — |
| 3d. The other client loads the same team | partly | Same API and team id for any client; snapshot proven per user | Proven on two real clients (web + mobile) once they call the API |

### Public contract

| Plan item | Status | Evidence | What's left |
|---|---|---|---|
| Fixtures/players: `kickoffUtc`, status, crests, scorers, nullable fields | partly | `FixtureSummary` has `kickoffUtc`, `status`, `stateId`, scores; teams and players have crests | Scorers and crests on fixtures; agree the field list with web (`kickoff` vs `kickoffUtc`) (PR 6) |
| Retire synthetic ratings | done | C# `/players` never had the made-up ratings (`players.js` did) | Web stops expecting them (PR 6) |
| Port `/standings`, `/player-rankings`, `/matches?fixtureId=` | open | Netlify functions only | PR 5 |
| News headlines and categories | open | No C# news endpoint | Skipped: `05-news-service.md` isn't available |
| Request/response examples from the controllers | partly | OpenAPI + Scalar in Development (`/openapi/v1.json`) | Examples and a versioned export (PR 7) |
| Structured 400 / 401 / 403 / 404 / 409 / 503 | done | `FantasyExceptionFilter`; audit live test returned each status as expected | — |
| Expose `ETag` to permitted browser origins | open | CORS policy has no `WithExposedHeaders` (`Program.cs`) | PR 2 |
| Atomic version checks on concurrent lineup/transfer writes | open | Version is read, checked and incremented in one transaction, but not atomically: two requests can both pass the check | PR 2 |
| Test two clients editing the same team | open | Stale If-Match → 409 proven (unit + live); no concurrent test | PR 2 |
| Export OpenAPI per build and version it; contract tests | open | — | PR 7 |
| Local launch target | done | `Properties/launchSettings.json`; `.env` loader | — |
| Staging/production config: scoped role, Supabase settings, explicit origins | partly | `Cors:AllowedOrigins` setting; scoped `app_api_user` | Staging/production values and a deploy target (PR 7) |
| Monitor database readiness and data freshness separately | partly | `/health` reports DB reachable + latest `sync_jobs` row | Split readiness from freshness (latest football update) (PR 7) |

### Scoring and prices

| Plan item | Status | Evidence | What's left |
|---|---|---|---|
| Confirm starter, bench and substitution codes | done | `SportmonksCodes`: lineup 11 = XI, 12 = bench; sub `player_id` comes on, confirmed from 2,489 subs (`2c7839e`) | — |
| Hand-check minutes, a substitute, captain points and a corrected goal | done | Audit §3: 25 players in 5 fixtures hand-checked, incl. 45+ stoppage subs and goals; rescinded goal, captain and vice-captain rules in unit tests (audit branch) | No rescinded goal exists in the data to check by hand |
| Assert expected results in tests before enabling scoring and price runs | done | 267 tests on the audit branch (70 new); minutes, stoppage, live states, captaincy decisions | Merge the audit PR |
| Finalizer uses `sync.gameweek_sync_status`; completeness rule for late facts and postponements | partly | `GameweekRules.CanFinalise`: `marked_complete` OR every fixture final/won't be played (postponed fix, audit branch) | Rule for late facts (events after full time); read `events_complete`/`stats_complete`; the sync doesn't fill this table yet (**needs sync team**) (PR 3) |
| Operations owner or scheduled job for gameweeks, finalising, rescoring, prices | open | Admin endpoints exist (`/admin/gameweeks`, `/score`, `/prices/*`) | Runbook or scheduled job (PR 8) |
| Test repeated scoring against Postgres | partly | Deterministic captain makes re-scores pick the same players (audit); update-not-insert logic and unique index by code review | Postgres test: finalise twice → same rows (PR 3) |
| Test duplicate transfer requests | open | — | Idempotency key or replay check + test (PR 2) |
| Test league capacity against Postgres | partly | Cap rule unit-tested; `SELECT … FOR UPDATE` on join | 20th/21st simultaneous join test on Postgres (PR 1/2) |
| Test two-device edits against Postgres | open | — | PR 2 |

### Release gate

| Gate | Status | Evidence | What's left |
|---|---|---|---|
| Both clients read the same team, picks and balance | partly | One API, one team per user per season; snapshot proven | Real web + mobile clients |
| A stale edit gets a recoverable conflict | done | 409 "Team changed" with the current team (audit, unit + live) | — |
| A non-admin can't score | done | `AdminService.RequireAdminAsync` on every admin route; live 403 on three admin endpoints (audit §4) | — |
| A final score and league rank match a hand-calculated fixture and stay stable after a rerun | partly | Player scores hand-checked on real data (audit §3); deterministic captaincy | Finalise a real gameweek on Postgres twice and compare (PR 3) |
| Keep previous reads available during rollout | done | Netlify functions stay until each C# endpoint replaces them | — |
| A single writer per team | partly | Writes need the owner's token; one team per user per season | Atomic version check (PR 2) |

### Needs sync team

- Fill `sync.gameweek_sync_status` (`events_complete`, `marked_complete`) so the finalizer can wait for complete data.
- Resume event and lineup updates (last written 1 Nov 2026) and, if possible, backfill 2025/26 lineups and events.
- Merge `flyway-catchup` and baseline/repair the shared database's Flyway history (V033–V038 were applied by hand as touchline-api 001–006).

## 2. Proposed order of work

Small PRs, highest risk first. Each lists what it proves. PR 0 isn't new work but unblocks the rest.

| # | PR | Why this order | Done when |
|---|---|---|---|
| 0 | Merge the audit PR (`audit/fantasy-2026-11-23`) and touchline-sync `flyway-catchup` | The scoring fixes and decisions, and the rerunnable migrations, are the base everything else tests against | Both merged; CI green including the 14 Postgres tests |
| 1 | **Disposable-Postgres test base**: fixture builds the full schema from `docs/migrations` 001–006 (clean install) and from an "old" schema + the same files (upgrade); tests connect as a role with `app_api_user`'s grants and RLS; first persistence tests (team, squad, transfers, league join cap with 21 concurrent joins) | Every later PR needs real-database tests; today only `/me` has them | Clean install and upgrade both pass; persistence tests pass with real grants |
| 2 | **Concurrency and offline**: `ETag` in `Access-Control-Expose-Headers`; atomic version check (`UPDATE … SET version = version + 1 WHERE id = @id AND version = @expected`, 0 rows → 409); idempotency for duplicate transfer requests (client `Idempotency-Key` header, or reject an identical replay against the same version); two-clients-same-team test | Lost updates and double transfers corrupt teams; hardest to fix after launch | Two parallel edits → exactly one 200 and one 409; a replayed transfer doesn't apply twice |
| 3 | **Finalizer completeness**: finalise only when `marked_complete`, or every fixture final/won't be played *and* no new events for N hours after the last full time (setting); re-finalise picks up late facts; Postgres test: finalise twice → identical rows | Final points must be right and stable | Repeated scoring identical; late events change scores only through a re-score |
| 4 | **Profile features** (section 3) + migration (touchline-sync V039 / docs/migrations 007) | Needed by both clients' onboarding | All rules tested; standings show display name and team name |
| 5 | **Port `/standings`, `/player-rankings`, `/matches?fixtureId=`** from the Netlify functions, same filters and tie rules | Last reads still served by Netlify | Responses match the functions for the same fixtures (snapshot tests) |
| 6 | **Response contract**: `kickoffUtc`, status, crests and scorers on fixtures, nullable fields documented; no synthetic ratings | Web and mobile need one shape | Web types updated; contract agreed |
| 7 | **OpenAPI export + contract tests; health split** (`/health/ready` = DB, `/health/data` = latest football update); staging/production config and allowed origins | Clients generate against a fixed contract; ops can see stale data | `openapi/v1.json` versioned in the repo and checked in CI |
| 8 | **Ops runbook or scheduled job**: who creates gameweeks, edits deadlines (new `PATCH /admin/gameweeks/{id}`), finalises, re-scores, applies prices | Nothing runs by itself today | Written runbook or job, owner named |
| 9 | ~~News contract~~ | Skipped: `docs/plans/05-news-service.md` isn't available | — |

## 3. Profile features (proposal)

Proposed now, built in PR 4. The user always comes from the token; validation errors are 400 ProblemDetails listing every broken rule.

### Display name

- Optional; set or change any time.
- 2–30 characters after trimming. Inner spaces collapsed to one; no control characters.
- **Not unique.** `username` stays the unique handle.
- Shown in standings (`displayName ?? username`) and returned by `GET /me`.
- Empty string or `null` in a PATCH clears it.

### Favourite club

- Optional; one club per user; change any time; `null` clears it.
- Must be a club with a fixture in the **current** PSL season (today: the 16 below). A club that later leaves the league stays stored but isn't offered for new picks.
- **ID choice: `football.teams.id`** (the internal id), with `sportmonksTeamId` also returned by `GET /clubs`. Why:
  - **Consistency:** every app table already references football's internal ids (`fantasy_squads.player_id` → `football.players.id`, `season_id` → `football.seasons.id`).
  - **Integrity:** a real foreign key (`REFERENCES football.teams (id)`) is possible, which a Sportmonks id isn't (it isn't the key).
  - **Stable in practice:** the sync upserts on `sportmonks_team_id` and keeps the row; there's exactly one row per Sportmonks id (313/313), rows dating back to July 2026.
  - **Risk:** if the sync ever rebuilt `football.teams`, internal ids could change. The FK would then block the rebuild rather than silently break profiles, and `sportmonksTeamId` lets us re-map.

**Mapping the web app's current selections.** Touchline_Main keeps the favourite club only in the browser (`localStorage`, `favoriteClubId` slugs from `frontend/src/constants/pslClubs.ts`), so there's no server data to migrate. After the user signs in with Supabase, the app maps its slug once and sends `PATCH /me { favouriteClubId }`.

| Web slug (`pslClubs.ts`) | Web name | `football.teams.id` | Sportmonks id | Current name |
|---|---|---|---|---|
| `amazulu` | AmaZulu FC | **69** | 6973 | AmaZulu |
| `chippa` | Chippa United | **83** | 13547 | Chippa United |
| `durban_city` | Durban City FC | **96** | 274102 | Durban City |
| `golden_arrows` | Golden Arrows | **81** | 1555 | Golden Arrows |
| `chiefs` | Kaizer Chiefs | **76** | 2541 | Kaizer Chiefs |
| *(missing)* | — | **90** | 274104 | Kruger United |
| `sundowns` | Mamelodi Sundowns | **28** | 6755 | Mamelodi Sundowns |
| `marumo` | Marumo Gallants | **77** | 143574 | Marumo Gallants FC |
| *(missing)* | — | **123** | 270332 | Milford |
| `pirates` | Orlando Pirates | **50** | 9960 | Orlando Pirates |
| `polokwane` | Polokwane City | **62** | 12946 | Polokwane City |
| `richards_bay` | Richards Bay FC | **72** | 133378 | Richards Bay |
| `sekhukhune` | Sekhukhune United | **66** | 251707 | Sekhukhune United |
| *(missing)* | — | **146** | 277354 | Siwelele |
| `stellenbosch` | Stellenbosch FC | **60** | 3103 | Stellenbosch |
| `ts_galaxy` | TS Galaxy | **65** | 228707 | TS Galaxy |
| `supersport` | SuperSport United | — | — | Not in 2026/27 |
| `capetowncity` | Cape Town City FC | — | — | Not in 2026/27 |
| `magesi` | Magesi FC | — | — | Not in 2026/27 |

- The three web clubs not in 2026/27 map to **no club**; the app asks those users to pick again. SuperSport United's PSL status is reported to have passed to Siwelele, but that isn't confirmed here, so it isn't mapped automatically.
- The web list is missing Kruger United, Milford and Siwelele; it should switch to `GET /clubs` instead of a hard-coded list.
- Durban City, Kruger United and Siwelele have no crest in `football.teams` yet; `/clubs` returns `crestUrl: null` for them.

### Team rename

- `PATCH /api/v1/fantasy/team { name }` renames the caller's team for the current season; 404 if they have none.
- 3–30 characters after trimming (creating a team should use the same rule; today it allows 1–50).
- Allowed any time, including after a deadline: the name isn't part of scoring.
- Not unique. Standings show the new name immediately.

### Endpoints

| Method and path | Body / response |
|---|---|
| `GET /api/v1/me` | adds `displayName` and `favouriteClub { id, sportmonksTeamId, name, crestUrl }` (null when unset) |
| `PATCH /api/v1/me` | `{ displayName?, favouriteClubId? }`. Only fields present are changed; `null` clears. Returns the updated profile. |
| `PATCH /api/v1/fantasy/team` | `{ name }` → the updated team |
| `GET /api/v1/clubs` | current PSL clubs: `{ id, sportmonksTeamId, name, shortCode, crestUrl }`, public (no token), sorted by name |

`PATCH /me` is preferred over `PUT /me/profile` because it updates part of the existing `/me` resource.

### Schema

touchline-sync `db/migrations/V039__app_users_profile.sql` and touchline-api `docs/migrations/007_users_profile.sql`, same SQL, safe to rerun:

```sql
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS display_name VARCHAR(30);
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS favourite_club_id INTEGER;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_users_favourite_club' AND conrelid = 'app.users'::regclass) THEN
        ALTER TABLE app.users ADD CONSTRAINT fk_users_favourite_club
            FOREIGN KEY (favourite_club_id) REFERENCES football.teams (id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_users_display_name' AND conrelid = 'app.users'::regclass) THEN
        ALTER TABLE app.users ADD CONSTRAINT ck_users_display_name
            CHECK (display_name IS NULL OR length(trim(display_name)) BETWEEN 2 AND 30);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_users_favourite_club ON app.users (favourite_club_id);
```

Team rename uses the existing `app.fantasy_teams.name` (VARCHAR(100)); no schema change. The "current PSL club" rule is checked in the API, not the database, because it changes every season.

### Tests (PR 4)

- Display name: 1 char → 400; 2 → ok; 30 → ok; 31 → 400; whitespace trimmed; inner spaces collapsed; null clears; two users may share a name; standings show it instead of the username.
- Favourite club: a current club → ok; a non-PSL team (e.g. Aalborg BK) → 400; an unknown id → 400; null clears; `GET /me` returns the club with its crest.
- Team rename: 2 chars → 400; 3 → ok; 30 → ok; 31 → 400; trimmed; no team → 404; another user can't rename it (the team always comes from the token); standings show the new name.
- `GET /clubs`: exactly the current-season clubs; works without a token.
- Migration 007 / V039: runs twice cleanly on disposable Postgres (PR 1's base).

### Also worth flagging (web app)

Touchline_Main's browser-only accounts (`authService.ts`) keep `passwordHash: payload.password` in `localStorage`, i.e. the password in plain text on the device. Moving the web app to Supabase Auth (which these endpoints assume) removes it.
