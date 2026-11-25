# Account policies

These are the policies for usernames, moderation, cooldown, recovery, deletion and suspension. The numbers are also returned to the app in `GET /api/v1/me` → `policies`, and are settings in the `Accounts` section of `appsettings.json`.

## Identity

- **The permanent key** is the verified Supabase subject (`sub`). It's checked for issuer, audience, signature and expiry against the environment's own project.
- **Things that can change:** a username, team name, email (including an Apple relay address) or device. None of them changes which manager or team a fan owns.
- **Email is optional.**
  - It is stored when a token carries one.
  - A stored email is never overwritten by a later token. A linked Apple relay address doesn't replace a Google address.
  - An account created without email gets one the first time a token carries it.
  - Before migration 009, a missing email is stored as `{sub}@no-email.touchline.invalid`. The address can't receive mail and is never shown.
- **First access is idempotent.** Repeated or simultaneous first calls create exactly one profile.

## Usernames

- **Characters:** 3–20 letters, digits or underscore, after Unicode NFKC normalisation. So full-width "ｐｓｌ" counts as "psl".
- **Uniqueness:** unique ignoring case and width. The canonical key (NFKC, then case-folded) has a unique index.
- **Reserved names:** admin, administrator, root, system, support, help, helpdesk, moderator, mod, staff, official, touchline, psl, api, me, null, undefined, anonymous, deleted, user and everyone. Also any name starting with `touchline`, `admin`, `deleted_`, `user_` or `official`.
- **First choice:** the generated `user_…` name is replaced by the first choice at any time (`onboarding.needsUsername`).
- **Cooldown:** after that, one change every **30 days** (`Accounts:UsernameCooldownDays`). `policies.usernameChangeAllowedFromUtc` says when the next change is allowed.
- **Conflicts:** a taken name, including a simultaneous claim lost to another manager, returns `409 {"code":"username_taken","title":"Choose another username"}`. The response never says who holds the name.
- **History:** every change is kept in `app.username_history` for moderation.

## Team names

- 3–30 characters after trimming, on create and rename (`PATCH /api/v1/fantasy/team`, owner only).
- Names may repeat; usernames tell managers apart.
- A rename keeps the team id, picks, league memberships and ranks. The old name goes to `app.fantasy_team_name_history`.

## Moderation

- Usernames and team names are public: they appear on leaderboards together with points. Nothing else about a manager is public.
- An admin can rename an offensive username or team name. Repeated abuse leads to suspension.
- The rename history tables are the audit trail. A full admin audit log arrives in Sprint 7.

## Recovery

| Situation | What happens | What support does |
|---|---|---|
| Lost access to one provider | Sign in with any other provider linked to the same Supabase user | Nothing |
| `409 legacy_email_unlinked`: a profile from before Supabase sign-in holds the email | Nothing is written; the old profile keeps its teams | Verify the fan owns the address (a mail round trip from the old account's address). Then set `auth_user_id` on the old row to the fan's Supabase user id, as the database owner. The fan signs in again and sees the old teams |
| `409 email_in_use`: another signed-in account holds the email | Nothing is written | The fan signs in with the original method and links the new provider in Supabase (Auth → identity linking) |
| No email on the account | Works normally | If the fan later wants email recovery, they add an email in Supabase; the next token fills it in |

## Recent sign-in

Export (`GET /me/export`) and deletion (`DELETE /me`) need a token issued within the last **10 minutes** (`Accounts:RecentAuthMinutes`). Otherwise the API returns `403 {"code":"reauthentication_required"}` and the app asks the fan to sign in again.

## Deletion (anonymisation; no rows are deleted)

`DELETE /api/v1/me` does the following, in one transaction:
1. **Username** becomes `deleted_{id}`. Email, favourite club and notification preferences are cleared. The account is marked inactive with `deleted_at`.
2. **Teams** are renamed `Deleted manager {teamId}`. Their picks, entries, points and league memberships stay, so historical standings and other managers' league tables remain correct. They show only the anonymous names.
3. **Owned leagues:** each active league the manager owns passes to the **oldest remaining active member** (earliest `joined_at`). A league with no other member is **archived** (`is_active = false`).
4. **The old sign-in:** the row keeps `auth_user_id`, so a token from the deleted Supabase user gets `403 account_deleted` instead of silently creating a new account.

**Supabase Auth steps (support / dashboard owner):**
- Within 30 days of the request, delete the Supabase Auth user (Dashboard → Authentication → Users → Delete user, or the Admin API `auth.admin.deleteUser`).
- This removes the provider identities and the email held by Supabase.
- Record the request date and the deletion date in the support log.

**Backups:** these keep the pre-deletion row until they expire. Retention is described in the privacy inventory (Sprint 7).

## Suspension

- An admin suspends or reinstates an account with `PUT /api/v1/admin/users/{id}/suspension { suspended, reason }`.
- **Suspended managers can read everything they could before, but every write returns `403 account_suspended`.** Writes include profile changes, squad, lineup, transfers, chips, leagues and team rename.
- They are **hidden from the public Overall league**. They stay visible in private leagues they belong to, where members already know them, and they still see their own Overall rank.
- `GET /me` shows `suspended: true`.
