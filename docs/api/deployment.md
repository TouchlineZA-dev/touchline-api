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
| `Fantasy__RequireTargetGameweek` | `true` | `true` (set `false` only during a client rollout) |

**Secrets:** these live in the host's secret store, never in files:
- the DB password inside the connection string;
- `Supabase__JwtSecret`, only for a legacy HS256 project.

## Health checks

| Route | Use | Output |
|---|---|---|
| `GET /api/v1/health` | uptime monitor | `status` and time only |
| `GET /api/v1/health/ready` | load balancer readiness | 200 / 503 |
| `GET /api/v1/health/freshness` | data-freshness alert, separate from readiness | newest football commit, its age, and `fresh` (48 h threshold) |

Details of a failing check go to the server log only.
