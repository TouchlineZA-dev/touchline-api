# touchline-api

The user-facing API for Touchline, the PSL fantasy football app. It's written in C# on ASP.NET Core (.NET 10).

The API reads match data that the sync worker writes to the database. It owns the fantasy game state: users, teams, squads, points, leaderboards and prices.

```
Sportmonks ─► touchline-sync (FastAPI / Netlify) ─writes─► Supabase Postgres ◄─reads/writes─ touchline-api ◄── React frontend
                                                           football  (sync-owned, READ)
                                                           sync      (sync-owned, READ)
                                                           app       (API-owned, READ/WRITE)
```

**The API talks only to the Supabase database** (plus Supabase Auth's public signing keys to check tokens). It never calls touchline-sync, its API or its Netlify functions, and never asks it to refresh data. The two share the database and nothing else: the sync writes `football` and `sync`, the API reads them and owns `app`. Database grants enforce who can write what. Live points are only as fresh as the last data the sync wrote.

## Layout

One ASP.NET Core project, `src/Touchline.Api`, organised by folder:

| Folder | What goes in it |
|---|---|
| `Controllers/` | Routes only: health, seasons, teams, players, fixtures, gameweeks, me. No logic beyond calling a service and shaping the HTTP response. |
| `Models/` | Database models for the `football`, `sync` and `app` schemas, plus the response models the API returns |
| `Data/` | `TouchlineDbContext`, which maps all three schemas and blocks writes to `football` and `sync` |
| `Services/` | Database reads and writes: football data, gameweeks, health, and the fantasy services (team, squad store, match facts, points, leagues, pricing, admin) |
| `Users/` | Supabase token validation, `CurrentUser` (who is calling) and `UserService` (the `app.users` row) |
| `Business/` | Rules that don't touch the database: Sportmonks codes, position decoding, match-status decoding |
| `Fantasy/` | Fantasy game rules and the price formula, as pure C# (squads, transfers, chips, scoring, leagues, valuation). All rule numbers are in `FantasyRules.cs`; see its README. |
| `Program.cs` | Startup: registers the database, services, auth and CORS |

Services are registered as concrete classes. Add an interface only when a second implementation actually exists.

`tests/Touchline.Api.Tests` holds the unit tests. Keep `Business/` and `Fantasy/` free of database code so they can be tested without one.

The auth tests in `tests/Touchline.Api.Tests/Auth` run the real pipeline with test-signed Supabase-style tokens. The `/api/v1/me` tests also use a throwaway PostgreSQL container (Testcontainers), built from the Flyway `app.users` DDL plus `docs/migrations/002_users_auth_link.sql`. They need Docker. Without it they are skipped locally, and CI sets `TOUCHLINE_REQUIRE_DB_TESTS=true` so they fail instead.

## Run locally

Prerequisite: the .NET 10 SDK.

```bash
cp .env.example .env    # then fill in the connection string and Supabase URL
cd src/Touchline.Api
dotnet run
```

In Development, `AddDotEnv()` in `Program.cs` reads the nearest `.env` (searching up from the current directory) into configuration. `.env` is gitignored; `.env.example` is the committed template. Real environment variables take precedence over the file. `dotnet user-secrets` still works if you prefer it, e.g. `dotnet user-secrets set "ConnectionStrings:Touchline" "..."`.

Then open http://localhost:5147/scalar for interactive API docs. The OpenAPI JSON is at `/openapi/v1.json`. Both are served only in Development.

Notes on these settings:

- **Never commit secrets.** Use `.env` (or user-secrets) locally. When hosting, use environment variables such as `ConnectionStrings__Touchline` and `Supabase__Url`.
- **Port 6543** is the Supabase transaction pooler, which suits a web API. Connect as `app_api_user`. `Program.cs` sets `No Reset On Close`, because the pooler hangs on the reset Npgsql otherwise sends when reusing a connection. That role can only read `football` and `sync`, and can read, insert and update `app`. It has no DELETE anywhere.
- **Legacy JWT secret.** If the Supabase project still signs tokens with the legacy shared secret instead of JWT signing keys, also set `Supabase:JwtSecret`.

## Endpoints (v1)

| Method and path | Auth | What it returns |
|---|---|---|
| `GET /api/v1/health` | none | Whether the database is reachable, plus the latest sync job |
| `GET /api/v1/seasons/current` | none | The current PSL season (Sportmonks competition 806) |
| `GET /api/v1/teams` | none | Clubs playing in the current PSL season (teams with a fixture in it) |
| `GET /api/v1/players` | none | Players with position, current club, price and `available` (pickable this season) |
| `GET /api/v1/fixtures?from=&to=` | none | Current-season fixtures with a status (Scheduled, Live, Finished or NoResult) |
| `GET /api/v1/gameweeks?seasonId=` | none | Gameweeks from `app.gameweeks`, with `readyToScore` taken from `sync.gameweek_sync_status` |
| `GET /api/v1/me` | Bearer | The caller's `app.users` row, created on first sign-in. Needs migration 002. |

### Fantasy (Bearer; the user always comes from the token)

All need migrations 001–005 (`docs/migrations`). Rules: [`src/Touchline.Api/Fantasy/README.md`](src/Touchline.Api/Fantasy/README.md).

| Method and path | What it does |
|---|---|
| `GET /api/v1/fantasy/team` | My team for the current season (404 if none) |
| `POST /api/v1/fantasy/team` | Create my team `{ name }` |
| `GET /api/v1/fantasy/team/snapshot` | Everything to show the team offline in one call |
| `GET /api/v1/fantasy/team/squad?gameweek=N` | Picks, formation, captaincy, bench (default: next gameweek) |
| `PUT /api/v1/fantasy/team/squad` | First full squad pick |
| `PUT /api/v1/fantasy/team/lineup` | Formation, starters, bench, captaincy for the next gameweek (`If-Match`) |
| `POST /api/v1/fantasy/team/transfers` | `{ out, in }` for the next gameweek; returns cost and free transfers left (`If-Match`) |
| `POST /api/v1/fantasy/team/chips` | `{ chip }`: `bench_boost`, `triple_captain`, `wildcard` |
| `GET /api/v1/fantasy/team/points?gameweek=N` | Points with a breakdown per player; provisional while live |
| `GET /api/v1/fantasy/leagues` | My leagues plus Overall, with my rank |
| `POST /api/v1/fantasy/leagues` | Create a private league `{ name }` |
| `POST /api/v1/fantasy/leagues/join` | Join with `{ code }` |
| `POST /api/v1/fantasy/leagues/{id}/leave` | Leave (not the owner) |
| `GET /api/v1/fantasy/leagues/{id}/standings?gameweek=N` | Standings; `overall` works as the id |

GET fantasy endpoints return an `ETag` and answer `304` to a matching `If-None-Match`. Validation failures are `400` ProblemDetails with every broken rule in `errors`.

### Admin (Bearer, `app.users.is_admin`)

| Method and path | What it does |
|---|---|
| `POST /api/v1/admin/gameweeks` | Create a gameweek (deadline defaults to 90 minutes before its first kickoff) |
| `POST /api/v1/admin/gameweeks/{id}/score` | Finalise points (only when marked complete or every fixture is final; safe to re-run) |
| `POST /api/v1/admin/transfer-windows` | Create `{ seasonId, name, opensOn, closesOn }` |
| `GET /api/v1/admin/transfer-windows?seasonId=N` | List windows |
| `POST /api/v1/admin/prices/preview?seasonId=N` | Dry-run prices plus backtest; saves nothing |
| `POST /api/v1/admin/prices/recalculate?windowId=N[&force=true]` | The once-per-window price run |
| `PUT /api/v1/admin/players/{id}/price-override` | `{ seasonId, expectedMinutes?, price?, reason }` |
| `PUT /api/v1/admin/players/{id}/available` | `{ available, reason, seasonId? }`: make a player pickable or not (needs 006) |

Paths match the existing Netlify functions, so the frontend can later point `VITE_API_BASE_URL` at this API. The response shapes are not identical yet:

- `/players` does not generate the made-up FIFA-style ratings that `players.js` returns.
- `/fixtures` does not yet list goal scorers.

## Auth

The frontend signs users in with Supabase Auth and sends `Authorization: Bearer <access token>`.

The API checks each token in three ways:

- **Signature:** verified against the project's public keys at `https://<project-ref>.supabase.co/auth/v1/.well-known/jwks.json`. If the legacy secret is configured, it is used as well.
- **Issuer:** must be `https://<project-ref>.supabase.co/auth/v1`.
- **Audience:** must be `authenticated`.

The user id always comes from the token's `sub` claim, never from the request body.

**Google and Apple.** Both are configured in the Supabase dashboard, not in this API. Provider client secrets, the Apple `.p8` key and the generated Apple client secret never belong here. The API only sees the Supabase access token, whichever provider the fan used.

**One profile per Supabase user.** `/api/v1/me` finds the `app.users` row by `auth_user_id = sub` and creates it on first sign-in; the fantasy endpoints do the same. The unique constraint on `auth_user_id` makes simultaneous first requests resolve to one row. Account linking is done in Supabase: a fan who links Apple to a Google account keeps the same `sub`, so they get the same profile and keep their teams. The stored email is not rewritten from later tokens, so Apple private relay addresses work.

**Email conflicts are 409, never auto-linked.** If the token's email (compared case-insensitively) already belongs to another row, nothing is written and the response is a problem-details `409` with a `code`:

| `code` | Meaning | Recovery |
|---|---|---|
| `legacy_email_unlinked` | A pre-Supabase row with no `auth_user_id` holds the email | Support links the row (sets `auth_user_id`), then the fan retries |
| `email_in_use` | Another signed-in Supabase user holds the email | Sign in the original way and link the new provider in Supabase |

To check a staging deployment end to end, see [`docs/staging-smoke-test-social-sign-in.md`](docs/staging-smoke-test-social-sign-in.md).

`app.users` (Flyway V014) had no column for the Supabase user id and required `password_hash`. [`docs/migrations/002_users_auth_link.sql`](docs/migrations/002_users_auth_link.sql) adds a unique `auth_user_id` and makes `password_hash` nullable (already applied to the shared Supabase database). Without it, `/api/v1/me` returns 503 with "Schema not ready"; the same happens if `auth_user_id` exists but `password_hash` is still `NOT NULL`. The football endpoints work without it.

## Database rules

- **The API's app-schema SQL lives in [`docs/migrations`](docs/migrations)** and is applied by hand in the Supabase SQL Editor, by an owner account, in number order (001, 002, …). Every file is safe to run twice. The `football` and `sync` tables come from touchline-sync's Flyway migrations; anyone running Flyway in touchline-sync must not recreate the tables these files create. Never run EF Core migrations against the shared database, and the API's own role can't create tables anyway.
- **`football` and `sync` are read-only.** `TouchlineDbContext` throws before saving any change to them, and the database role would reject the write anyway.
- **Two ID spaces.** App tables reference `football.players.id`. `match_events` and `player_lineups` reference `players.sportmonks_player_id`. Always join through `football.players`.
- **Final scores only.** Score a fixture only when `state_id` is 5, 7 or 8 (`MatchStatusExtensions.IsFinal`).
- **Times are UTC.** Convert to SAST (UTC+2) for display. Pass UTC values to Npgsql.
- **No deletes.** `app.player_price_changes` is append-only. Elsewhere, use flags instead of deleting.

## Fantasy status

The rules, settings and tests are done; see [`src/Touchline.Api/Fantasy/README.md`](src/Touchline.Api/Fantasy/README.md). The fantasy tables are in [`docs/migrations/003_fantasy_core.sql`](docs/migrations/003_fantasy_core.sql). The frontend's `20261122090000_fantasy_api.sql` is not our schema.

Before fantasy works end to end, run `docs/migrations` 001 (price-table grants), 002 (auth link), 003 (fantasy core) 004 (2026/27 gameweeks) and 005 (row-level security policies for the API) in the Supabase SQL Editor. Until then the fantasy endpoints return 503 "Schema not ready", and `/players` fails until 001 is run.

The Sportmonks lineup and substitution codes were confirmed against the database on 2026-11-22 (see `Business/SportmonksCodes.cs`).

Live points can only be as fresh as the football data. As of 2026-11-22 the sync writes fixtures, events and lineups in daily batches around 23:00 UTC, about a day after kickoff, so "live" points currently update once a day.

Prices: a player with no `app.player_prices` row now shows his position's minimum price (Fantasy:Pricing) instead of the old 50.0, which couldn't work with a R100.0m budget.
