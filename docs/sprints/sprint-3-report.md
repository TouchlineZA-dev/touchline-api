# Sprint 3 report: account and team onboarding

**Branches and status**
- **touchline-api:** branch `sprint/3-accounts`, stacked on `sprint/2-season-rules`.
- **touchline-sync:** branch `sprint/3-migrations`, stacked on `sprint/2-migrations`.
- **Tests:** 369 passed, 0 skipped, 0 failed, including the PostgreSQL-backed account tests (`Accounts/AccountTests.cs`, 16 tests) and the updated auth tests.

## "Pass when" (code side)

| Item | Evidence | Status |
|---|---|---|
| Renaming the username or team keeps both ids and standings | `Renaming_the_username_keeps_the_account_and_team_ids_and_respects_the_cooldown`, `Team_names_are_3_to_30_characters_may_repeat_and_renames_keep_the_id_with_history`. Standings key on team id; the name is only displayed | done |
| Test: conflicting names | `Username_is_unique_ignoring_case_and_unicode_width_and_the_conflict_reveals_no_holder`, `Simultaneous_claims_on_one_username_give_one_winner_and_one_stable_conflict`, `Invalid_or_reserved_usernames_are_rejected` | done |
| Test: missing email | `Token_without_email_creates_one_profile_and_a_later_email_fills_it_in` (replaces the old "no email → 400" test) | done |
| Test: linking a second identity | `Linked_google_and_apple_identities_share_one_profile_and_its_teams` (existing, kept) | done |
| Test: repeated first-access calls | `Repeat_sign_in_returns_the_same_profile`, `Concurrent_first_requests_create_exactly_one_profile`, `Losing_a_first_sign_in_race_returns_the_winning_profile` | done |
| Test: expired tokens | `Expired_token_is_401` (TokenValidationTests), `Expired_tokens_are_refused` | done |
| Test: deleted accounts | `Deleting_anonymises_passes_league_ownership_archives_empty_leagues_and_blocks_the_old_sign_in` | done |
| Test: suspended accounts | `Suspended_managers_can_read_but_not_write_and_are_hidden_from_overall` | done |
| Provider checklist ready for the staging round trip | `docs/accounts/provider-checklist.md` | done; the round trip is a human item |
| Identity: `sub` is the key; email optional and stored when present; 409 recovery codes kept; linking flow; recovery path documented; issuer/audience/signature/subject pinned | `UserService`; `docs/accounts/policies.md` § Identity, § Recovery; TokenValidationTests | done |
| Username: 3–20 chars, NFKC + case fold, unique index, reserved names, `PATCH /me` → `{username, onboarding}`, 409 `username_taken` without the holder, cooldown, history, visible policies | `Users/UsernameRules.cs`, `Users/AccountService.cs`, migration 009, `GET /me` → `policies` | done |
| Favourite club + `GET /api/v1/clubs` + club mapping verified against the DB | `Favourite_club_must_play_in_the_current_season_and_can_be_cleared`; `docs/accounts/club-mapping.md` (all 16 ids checked read-only on 2026-11-23) | done |
| Team name 3–30, owner-only rename, history | `PATCH /api/v1/fantasy/team` | done |
| Onboarding `{needsUsername, needsTeam, needsSquad}` | `Onboarding_moves_from_username_to_team_to_squad` | done |
| Notification preferences GET/PATCH | `Notification_preferences_default_on_and_can_be_changed` | done |
| Export and deletion (anonymisation), recent auth, Supabase steps documented | `Export_and_delete_need_a_recent_sign_in`; deletion test; policies § Deletion | done |
| Suspension flag; read but not write; hidden from public leaderboards | `PUT /admin/users/{id}/suspension`; suspension test | done |
| Leaderboards: username and team name only | `StandingRowView` has only team name and username; the suspension test checks rows carry no email | done |

## Phatu items covered this sprint

- **03 Stage 3:** items 1–5 and the pass criteria (code side).
- **01 Gate 5:** 5.1 as a checklist; 5.2–5.5 and the pass tests.
- **02 "Deliver the first end to end flow" §2:**
  - same Supabase project as both clients, plus issuer, audience, signature and subject (checklist plus existing token tests);
  - 409 recovery kept;
  - profile update for display name (now the username) and favourite club;
  - team rename;
  - stable internal club ids with the web-club mapping.
- **01 regressions:** "Account links to a second provider or changes name"; "Manager deletes an account while owning a league".
- **03/01 Stage 6 table:** `PATCH /api/v1/me` and `PATCH /api/v1/fantasy/team`, the example request/response and the conflict body, exactly as proposed.

**Items I added that Part 2 didn't list:**
- **01 Gate 5.2:** "A username, team name, relay email or device identifier can change." A stored email is never overwritten by a later (e.g. relay) token; a missing email is filled in later.
- **03 Stage 3.5:** "Publish only the public handle and team display name on leaderboards" is now tested.

## What's left

- **Sprint 6:** the end-to-end staging flow (two managers, one league); the OpenAPI examples for these endpoints.
- **Sprint 7:**
  - admin audit log (who renamed or suspended whom);
  - rate limits on renames and sign-in calls;
  - privacy inventory linked to export and deletion;
  - JWKS key-rotation test (exists in part).
- **Human:** the staging round trip for all three providers, and the mobile owner's sign-off.

## Needs sync team

The list in sprint-1-report and sprint-2-report still applies. New:

1. **Missing short codes:** `football.teams.short_code` is NULL for Polokwane City, Kruger United, Durban City, Milford and Siwelele.

## Needs other owners

1. **Supabase dashboard owner:** everything in `docs/accounts/provider-checklist.md`, including:
   - separate staging and production credentials;
   - callback allowlists and mobile return URLs;
   - Google clients;
   - Apple key and secret rotation owner;
   - Facebook Live mode and the email permission;
   - identity linking enabled;
   - MFA for production admins.
2. **Mobile owner:** sign off the callback round trip (Google, Apple relay first and repeat, Facebook without email, linking, a second device).
3. **Web owner:**
   - map the stored club slugs with `docs/accounts/club-mapping.md`;
   - add Kruger United, Milford and Siwelele to the club picker;
   - stop storing passwords in `localStorage` (`authService.ts`, flagged earlier);
   - move display-name editing to `PATCH /me { username }`.
4. **Support:** handle the `legacy_email_unlinked` linking procedure and Supabase Auth user deletion within 30 days of a `DELETE /me` (`docs/accounts/policies.md`).
5. **Sportmonks subscription:** still on the free plan. (Carried over.)

## Conflicts

1. **Team name length:** the reviewed API allowed team names of 1–50 characters. The runbook's agreed range is 3–30, which is now enforced on create and rename. Existing longer or shorter names stay until renamed.
2. **The old "token without email → 400" behaviour** (and its test) is replaced by the runbook's "stop requiring an email claim".

Neither is a conflict with Part 3 rules.

## Decisions for the team

1. **Generated usernames:** `user_<32 hex>` counts as "no username chosen" (`needsUsername`). The first choice is exempt from the 30-day cooldown.
2. **Reserved names and prefixes:** the list is in `policies.md`. It can be extended in `UsernameRules.Reserved`.
3. **Email:**
   - A stored email is never overwritten by a later token, which keeps the previous decision from the auth work.
   - A missing one is filled in from a later token.
   - Before 009, a missing email is stored as `{sub}@no-email.touchline.invalid` and never shown.
4. **Recent sign-in:** 10 minutes for export and deletion, returning `403 reauthentication_required`. Username changes aren't gated, because they're common right after sign-up.
5. **Deletion:**
   - league ownership passes to the oldest remaining **active** member, and a league with no other member is archived;
   - the deleted manager's teams stay in league tables as "Deleted manager {id}";
   - `auth_user_id` is kept so the old login gets `403 account_deleted`.
   - Hard deletion of the Supabase Auth user is a support step (no row deletes in `app`, rule 3).
6. **Suspension:** hidden from the **Overall** league only, staying visible in private leagues, and still sees their own Overall rank. All writes return `403 account_suspended`, including `PATCH /me` and notification changes.
7. **Unauthenticated `/me`:** a token without a valid `sub` now returns `401 invalid_subject` through the shared problem-details filter, so every account error has a `code`.

## Migration file names

- touchline-api: `docs/migrations/009_accounts.sql`
- touchline-sync: `db/migrations/V041__accounts.sql`

Not applied. Safe to rerun. Before 009:
- sign-in still works, including without email (placeholder);
- `GET /me` works (onboarding falls back to the generated-name check);
- `GET /me/notifications` returns the defaults and export works;
- `PATCH /me`, `PATCH /me/notifications`, delete and suspension return `503 Schema not ready`.

## PR links

- touchline-api: https://github.com/TouchlineZA-dev/touchline-api/compare/sprint/2-season-rules...sprint/3-accounts?expand=1 (base: `sprint/2-season-rules`)
- touchline-sync: https://github.com/TouchlineZA-dev/touchline-sync/compare/sprint/2-migrations...sprint/3-migrations?expand=1 (base: `sprint/2-migrations`)
