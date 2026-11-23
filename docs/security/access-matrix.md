# Access matrix

**Roles:**

| Role | Meaning |
|---|---|
| A | anonymous fan, no token |
| M | manager (signed in) |
| O | league owner |
| S | support (an admin today; no separate role yet) |
| Op | operator (admin) |
| W | sync worker (no API access; Postgres only) |
| Ad | admin (`app.users.is_admin`) |

**How it's enforced:**
- The API role's RLS policies are broad (`api_full_access` on every app table), so **ASP.NET enforces ownership and membership itself**:
  - every fantasy and account call resolves the manager from the verified token `sub`, never from the body;
  - league routes check membership or ownership;
  - admin routes pass an authorization filter (`AdminAuditFilter`) before model binding.
- **Tests:**
  - `TokenValidationTests.Only_me_fantasy_and_admin_require_authentication` (every route classified);
  - `SecurityTests.Access_matrix_…` (every admin route: anonymous 401, manager 403, refusal audited);
  - `LeagueTests` (owner and member rules);
  - `AccountTests` (suspended, deleted);
  - `SafeWriteTests` (own team only).

**Cell values:**
- ✓ allowed;
- own: only the caller's own data;
- mem: league members only;
- own+: the owner only;
- 401: needs a token;
- 403: refused.

| Endpoint | A | M | O | Ad / Op / S | W |
|---|---|---|---|---|---|
| `GET /api/v1/fixtures`, `/fixtures/{id}`, `/matches`, `/standings`, `/player-rankings`, `/players`, `/players/{id}`, `/clubs`, `/clubs/{id}`, `/teams`, `/gameweeks`, `/seasons/current`, `/rules`, `/health*` | ✓ (public cache) | ✓ | ✓ | ✓ | – |
| `GET/PATCH/DELETE /api/v1/me`, `/me/notifications`, `/me/export` | 401 | own (writes 403 if suspended; export and delete need a recent sign-in) | own | own | – |
| `GET/POST/PATCH /api/v1/fantasy/team` (+ squad, lineup, snapshot, points, explain, history, chips, transfers, preview, confirm, ledger, autopick) | 401 | own team only | own | own | – |
| `GET /api/v1/fantasy/leagues`, `POST` (create), `POST join`, `POST {id}/leave` | 401 | own memberships | ✓ | own | – |
| `GET /api/v1/fantasy/leagues/{id}/standings`, `/{id}/history` | 401 | mem (Overall: everyone signed in; suspended managers hidden) | mem | mem | – |
| `POST /api/v1/fantasy/leagues/{id}/owner`, `/{id}/archive` | 401 | 403 | own+ | 403 unless owner | – |
| Join codes | – | owner only, in the owner's league list | ✓ | – | – |
| Another manager's squad, picks, email or join code | ✗ (no route) | ✗ (no route) | ✗ | ✗ through the API | – |
| `/api/v1/admin/*` (gameweeks, deadline, fixture moves, lock, settlement, resolutions, score, corrections, revisions, prices, windows, overrides, availability, suspension, staging kit) | 401 | 403 | 403 | ✓, each call audited | – |
| Manual sync | not in the API (rule 1) | – | – | – | Postgres only |

Support and operator are the admin role today. A narrower support role (read-only explanation of any team, no scoring) is listed under "decisions for the team".

## Effective database permissions (live, read-only check 2026-11-23)

Query: `docs/security/effective-permissions.sql`, run as `app_api_user` (catalog functions only).

| Role | football | sync | app |
|---|---|---|---|
| `app_api_user` | SELECT on all 9 | SELECT on all 4 | SELECT, INSERT on all 17; UPDATE on 16 (`player_price_changes` is insert-only); **no DELETE anywhere** |
| `sync_worker_user` | SELECT, INSERT, UPDATE on all 9 | SELECT, INSERT, UPDATE on all 4 | **More than designed**: SELECT on `gameweeks` (expected) **and** `users`, `fantasy_teams`, `fantasy_squads`; SELECT, INSERT, UPDATE on `leaderboards`, `player_prices`, `points_results` |
| `anon` | none | none | none |
| `authenticated` | none | none | none |

**RLS** is on for the V035/V038 fantasy tables. It's off on `users`, `fantasy_squads`, `fantasy_teams`, `gameweeks`, `leaderboards`, `news_cache`, `player_prices`, `player_price_changes` and `points_results`. That's harmless while `anon` and `authenticated` have no grants, but the RLS policies (V037 pattern) should be added if the Data API is ever exposed.

**Action (needs sync team / ops):**
- Revoke the worker's SELECT on `app.users`, `app.fantasy_teams` and `app.fantasy_squads` (personal data).
- Revoke its INSERT and UPDATE on `app.points_results`, `app.player_prices` and `app.leaderboards` (fantasy results belong to the API).
- Add a migration and re-run the query.
