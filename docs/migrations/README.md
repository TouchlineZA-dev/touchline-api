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
| `007_gameweek_calendar.sql` | Persisted gameweek numbers and the insert-only fixture→gameweek assignment history (Sprint 1, not applied yet) |
| `008_rules_versions.sql` | Rules and points-table versions on gameweek entries and points results (Sprint 2, not applied yet) |
| `009_accounts.sql` | Optional email, usernames, favourite club, suspension and deletion flags, rename history, notification preferences (Sprint 3, not applied yet) |
| `010_safe_writes.sql` | Receipts with idempotency keys, granted free transfers, the locked deadline snapshot, cancellable chips (Sprint 4, not applied yet) |
| `011_settlement.sql` | Gameweek state machine, fixture resolutions, score revisions and corrections (Sprint 5, not applied yet) |
| `012_admin_audit_log.sql` | Insert-only audit log of every admin route call (Sprint 7, not applied yet) |
| `015_wizard_drafts.sql` | Wizard drafts: save and resume unfinished input per manager (onboarding, squad, lineup, transfers, league). Cleared by UPDATE, never deleted (audit 25 Nov 2026, not applied yet) |

touchline-sync keeps the same SQL as Flyway versions, so a Flyway run there is a no-op for anything already applied here:

| Here | touchline-sync |
|---|---|
| 001 | V034 |
| 002 | V033 |
| 003 | V035 |
| 004 | V036 |
| 005 | V037 |
| 006 | V038 |
| 007 | V043 |
| 008 | V044 |
| 009 | V045 |
| 010 | V046 |
| 011 | V047 |
| 012 | V048 |
| 013, 014 | not used (V049 and V050 are the sync owner's) |
| 015 | V051 |

Change both copies together. From 007 on, file NNN is Flyway V(NNN+36). V039–V041, V049 (operations, renamed from V042) and V050 (API handoff fixes) are the sync owner's, so 013 and 014 are skipped here. The API's database tests apply the pinned Flyway set (`tests/db/flyway`) and then these files from 007 on, so a new file here is tested before it reaches touchline-sync.

The API code works before a new file is applied: it checks for new tables and columns (`Data/SchemaFeatures.cs`) and falls back to the old behaviour.

Rules for these files:

- Every file is safe to run twice (`IF NOT EXISTS`, guarded constraints, repeatable grants, seed rows skipped if present).
- Only the `app` schema and grants. Never write to `football` or `sync`. No `DELETE` or `DROP` on data tables.
- The `football` and `sync` schemas come from touchline-sync's Flyway migrations. The app-schema files above are mirrored there and are safe to rerun, so Flyway doesn't recreate or break anything applied by hand.
- If the SQL Editor offers to enable row-level security on new tables, any new table also needs a policy for `app_api_user` (see 005), or the API can't read or write it.
- Add new changes as the next number. Don't edit a file that has already been run; write a new one.
