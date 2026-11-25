# Staging kit (for the mobile developer)

The staging kit is a stable, resettable set of data for testing the app against staging. It is admin-only and staging-only.

## What it gives you

`POST /api/v1/admin/staging-kit/reset` (needs an admin token) returns:
- **Two reference managers**, each with a team ("Reference XI 1/2").
  - When `StagingKit:ManagerSubjects` lists two Supabase user ids from the staging project, those users can sign in as the managers (`canSignIn: true`).
  - Otherwise the managers exist only as league members.
- **A private league** ("Staging kit league") with both teams, and its join code.
  - Each reset **archives** the previous kit league and creates a new one. Nothing is deleted (`archivedLeagueIds`).
- **Stable fixture ids:**
  - the reference fixture 1393 (Pirates 2–1 Chiefs, GW9), which never changes;
  - the current or next gameweek's fixture ids.

## Enabling it

| Setting | Value |
|---|---|
| `StagingKit__Enabled` | `true` in staging only. It is **refused in Production** whatever the setting. |
| `StagingKit__ManagerSubjects__0`, `__1` | Supabase user ids of the two staging test accounts. The staging Supabase owner creates them and shares the passwords through the password manager. |

When disabled, the route answers 404.

## Test flow for the app

1. Sign in as reference manager 1 and pick a squad (`POST /fantasy/team/autopick/preview`, then `PUT /fantasy/team/squad` with `gameweekId`).
2. Sign in as manager 2 on a second device and do the same.
3. Open the kit league's standings, make a transfer, and check points and the explanation.
4. Open fixture 1393 for a complete finished-match payload.
5. Reset the kit whenever a clean league is needed.
