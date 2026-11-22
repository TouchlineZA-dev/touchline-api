# Proposed migrations

touchline-sync owns all database DDL (Flyway, `db/migrations`). Schema changes for this API are written here first, then moved there.

Moved so far (branch `fantasy-migrations` in touchline-sync):

| touchline-sync file | What it does |
|---|---|
| `V033__app_users_supabase_auth_link.sql` | `app.users.auth_user_id`, `password_hash` nullable (already in touchline-sync main) |
| `V034__app_price_table_grants.sql` | Grants `app_api_user` access to `app.player_prices` and `app.player_price_changes` |
| `V035__fantasy_core.sql` | Fantasy tables and columns (was `V034__fantasy_core.sql` here) |
| `V036__seed_gameweeks_2026_27.sql` | The 29 gameweeks of the 2026/27 PSL season |

Don't keep a second copy here: edit the touchline-sync files.
