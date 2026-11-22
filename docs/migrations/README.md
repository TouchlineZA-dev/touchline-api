# Migrations for the API's app schema

These SQL files create and change the `app` tables that touchline-api owns. They are **applied by hand in the Supabase SQL Editor**, by an owner account (postgres), **in number order**. The API's own database role (`app_api_user`) can't run them.

| File | What it does |
|---|---|
| `001_grants_player_prices.sql` | Gives `app_api_user` access to `app.player_prices` and `app.player_price_changes` (fixes `/players`) |
| `002_users_auth_link.sql` | `app.users.auth_user_id`; `password_hash` nullable |
| `003_fantasy_core.sql` | Fantasy tables and columns; budget default 100.0 |
| `004_seed_gameweeks_2026_27.sql` | The 28 gameweeks of the 2026/27 PSL season |
| `005_rls_policies_for_api.sql` | Policies so `app_api_user` can use the 003 tables with row-level security on |
| `006_player_availability_overrides.sql` | Admin overrides for which players can be picked and priced (insert only) |

Rules for these files:

- Every file is safe to run twice (`IF NOT EXISTS`, guarded constraints, repeatable grants, seed rows skipped if present).
- Only the `app` schema and grants. Never write to `football` or `sync`. No `DELETE` or `DROP` on data tables.
- The `football` and `sync` schemas come from touchline-sync's Flyway migrations. Anyone running Flyway there must not recreate the tables created here.
- If the SQL Editor offers to enable row-level security on new tables, any new table also needs a policy for `app_api_user` (see 005), or the API can't read or write it.
- Add new changes as the next number. Don't edit a file that has already been run; write a new one.
