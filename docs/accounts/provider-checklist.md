# Sign-in provider checklist (staging round trip)

**Owners:**
- the Supabase dashboard owner, for the project configuration;
- the mobile owner, for native callbacks and the sign-off.

The API only validates Supabase access tokens, so nothing on this list is done in this repo. The API side is done and tested:
- the verified `sub` is the account key;
- email is optional;
- linked identities share one profile;
- 409 recovery codes;
- issuer, audience, signature and expiry are pinned per environment.

Tick each box against the **staging** Supabase project first, then repeat for production.

## Both environments

- [ ] Separate Supabase projects (or at least separate provider credentials) for staging and production. Each API environment's `Supabase__Url` points at its own project, so tokens from the other project are rejected (`Token_from_another_supabase_project_is_401`).
- [ ] Web and mobile clients use the **same** Supabase project per environment as the API.
- [ ] Redirect / callback allowlist in Supabase Auth → URL Configuration:
  - the web origins (staging and production);
  - the mobile deep-link return URLs (Capacitor and Expo schemes);
  - no wildcards in production.
- [ ] JWT signing: the project uses asymmetric signing keys, so the API reads JWKS. `Supabase__JwtSecret` stays empty unless the project is still on the legacy shared secret.
- [ ] Identity linking is enabled (Auth → Settings → manual linking), so a second provider joins the same Supabase user and therefore the same Touchline manager.
- [ ] MFA required for production dashboard admins.

## Google

- [ ] OAuth clients exist for web, iOS and Android, as needed. The client ids are entered in Supabase's Google provider (authorised client ids).
- [ ] The consent screen is published (not "testing"), or staging testers are added.
- [ ] Round trip: sign in → `GET /api/v1/me` returns a profile → sign in on a second device → same profile id.

## Apple

- [ ] Services ID, key id, team id and the private key are configured in Supabase.
- [ ] The web OAuth secret (the JWT generated from the key) expires after at most 6 months. **Rotation owner:** _______ ; next rotation date: _______.
- [ ] First authorisation with "Hide My Email": the token carries a `privaterelay.appleid.com` address. The profile is created (`Apple_private_relay_email_creates_a_profile`).
- [ ] Repeat sign-in: Apple sends no name or email after the first time. The same profile is returned (keyed on `sub`).
- [ ] Linking Apple to an existing Google account keeps the Google email on the profile (`Linked_google_and_apple_identities_share_one_profile_and_its_teams`).

## Facebook

- [ ] The app is in Live mode. Test with an **ordinary user outside the app's developer and tester roles**.
- [ ] The `email` permission is requested. Also test a user who declines it, or who has no email on Facebook: the token has no email claim and the API still creates the profile (`Token_without_email_creates_one_profile_and_a_later_email_fills_it_in`).

## Recovery and conflicts (support runbook)

- [ ] `409 legacy_email_unlinked`: an old profile (from before Supabase sign-in) holds the email. Support links it by setting `app.users.auth_user_id` to the fan's Supabase user id, after verifying ownership. See `docs/accounts/policies.md` § Recovery.
- [ ] `409 email_in_use`: another signed-in account holds the email. The fan signs in with their original method and links the new provider in Supabase.
- [ ] `403 account_deleted`: the account was deleted. Support confirms the Supabase Auth user was removed as well (`policies.md` § Deletion).

## Sign-off

| Check | Staging result | Date | Who |
|---|---|---|---|
| Google round trip and second device | | | |
| Apple relay, first and repeat sign-in | | | |
| Facebook ordinary user, with and without email | | | |
| Link a second identity, same team after | | | |
| Mobile owner signs off the callback round trip | | | |
