# Running and deploying the API (.NET 10)

## Local

1. Copy `.env.example` to `.env` (git-ignored) and fill in:
   - `ConnectionStrings__Touchline` (pooler 6543, `app_api_user`);
   - `Supabase__Url`.
2. Run `dotnet run --project src/Touchline.Api --launch-profile http`. This serves `http://localhost:5147`, with the Scalar UI at `/scalar` and the OpenAPI document at `/openapi/v1.json`.
3. **Tests:** `dotnet test Touchline.sln`.
   - Database tests need `TOUCHLINE_TEST_PG` (an admin connection string to a throwaway PostgreSQL server) or Docker.
   - CI requires them: `TOUCHLINE_REQUIRE_DB_TESTS=true`.

## Staging and production

**Settings:** all come from environment variables. Nothing environment-specific is committed.

| Setting | Staging | Production |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Staging` | `Production` |
| `ConnectionStrings__Touchline` | staging DB, role `app_api_user` (SELECT/INSERT/UPDATE on `app`, SELECT on `football`/`sync`, no DELETE) | production DB, same scoped role |
| `Supabase__Url` | the staging Supabase project (the same one the staging web and mobile clients use) | the production project |
| `Supabase__Audience` | `authenticated` | `authenticated` |
| `Cors__AllowedOrigins__0…n` | staging web origins, e.g. `https://staging.touchline…` | production web origins only; no wildcards |
| `OpenApi__Expose` | `true` (the mobile developer reads the contract) | optional |
| `StagingKit__Enabled` | `true` | never (refused anyway) |
| `Fantasy__Budget` | **not set** (default R100.0m; startup log shows `Fantasy budget: 100.0`) | **not set** |
| `Fantasy__RequireTargetGameweek` | `true` | `true` (set `false` only during a client rollout) |

**Secrets:** these live in the host's secret store, never in files:
- the DB password inside the connection string;
- `Supabase__JwtSecret`, only for a legacy HS256 project.

**Required at startup:** in Staging and Production the app refuses to start unless these are set. The error names the setting, never its value.

| Variable | Rule |
|---|---|
| `ConnectionStrings__Touchline` | Must include `SSL Mode=Require` (or stricter). Username must be `app_api_user`, never `postgres` or `sync_worker_user` |
| `Supabase__Url` | `https://<project>.supabase.co` |
| `Cors__AllowedOrigins__0` | At least one explicit `https://` web origin; no wildcards. Add `__1`, `__2` … for more |

- **Defaults:** `ASPNETCORE_ENVIRONMENT` defaults to `Production` in the Docker image.
- **Optional:** `PORT`, `Supabase__JwtSecret` (legacy projects only), `OpenApi__Expose`.
- **Never in Production:** `StagingKit__Enabled=true` (refused).

## Railway

The repo deploys with the `Dockerfile` (`railway.toml`: `builder = "DOCKERFILE"`).

**Port**
- The app listens on `PORT` when Railway sets it, otherwise on **8080**.
- The service's public domain must target **the same port**. A mismatch gives Railway 502s.
- Safe default: set the variable `PORT=8080` and set the domain's target port to `8080`.
- An explicit `ASPNETCORE_URLS` overrides this; don't set it on Railway.

**Health check**
- `railway.toml` sets `healthcheckPath = "/api/v1/health/live"`. That route returns 200 whenever the process is up.
- So a database outage or stale football data never blocks a deploy or restarts the container.
- Use `/api/v1/health` for uptime monitoring.

**Image checks in CI**
- The `docker` job builds the image on every PR.
- It then starts the image in Production with placeholder settings and an unreachable database, and checks `/health/live` is 200 and `/health` is 503.
- `.dockerignore` must never exclude `Directory.Build.props` or `src/`. A test checks every `COPY` source.

**When a Railway deploy fails, check in this order**
1. **Build logs:** a missing `COPY` source or restore error. CI's `docker` job would have failed first.
2. **Deploy logs:** `Refusing to start in Production: …` names the missing variable.
3. **Port:** a 502 with a running container means the domain's target port isn't the app's port.

## Health checks

| Route | Use | Output |
|---|---|---|
| `GET /api/v1/health/live` | host liveness (Railway health check) | always 200 while the process runs; no database call |
| `GET /api/v1/health` | uptime monitor | 200 `ok` (database up, data fresh); 200 `degraded` (database up, data stale or a live fixture's poller stalled); 503 `unavailable` (database unreachable). `status` and time only |
| `GET /api/v1/health/ready` | load balancer readiness | 200 / 503 on database reachability |
| `GET /api/v1/health/freshness` | data-freshness alert, separate from readiness | newest football commit, its age, and `fresh` (48 h threshold) |

Details of a failing check go to the server log only.
