# Staging smoke test: Google and Apple sign-in

This checks that a fan who signs in with Google or Apple through Supabase gets exactly one Touchline profile from `GET /api/v1/me`, and that linking a second provider keeps that profile.

Run it against **staging only**. Every value in `<angle brackets>` is a placeholder. Never paste real tokens, keys or passwords into this file, tickets, chat, commits or CI logs.

## 0. Before you start

**Database.** `docs/migrations/002_users_auth_link.sql` (the old V033) must be applied to the staging database, by hand in the Supabase SQL Editor as an owner account. Until then, `/api/v1/me` returns `503 Schema not ready`. Confirm it with a read-only query as an admin role:

```sql
SELECT column_name, is_nullable
FROM information_schema.columns
WHERE table_schema = 'app' AND table_name = 'users'
  AND column_name IN ('auth_user_id', 'password_hash');
-- expect: auth_user_id YES, password_hash YES

SELECT conname FROM pg_constraint
WHERE conrelid = 'app.users'::regclass AND contype = 'u';
-- expect uq_users_auth_user_id among the results
```

**Supabase dashboard (staging project).** This is outside the API, and none of these values go in this repo:

- Authentication → Providers → **Google**: enabled, with the Google OAuth client ID and secret.
- Authentication → Providers → **Apple**: enabled, with the Services ID, Team ID, Key ID and the client secret generated from the `.p8` key. The Apple client secret expires after at most 6 months, so put a renewal reminder in the calendar.
- Authentication → URL Configuration: the redirect URL used below (e.g. `http://localhost:3000`) is in the allow list.
- Authentication → Settings: **manual linking** enabled (needed for step 4).
- Authentication → Sign In / Providers: decide whether to allow automatic linking. With automatic linking, Supabase links identities that share a *verified* email. It does not link an Apple "Hide My Email" relay address to a Google address, so those fans must link manually (step 4).

**API configuration (staging host environment variables):**

```
ConnectionStrings__Touchline=Host=<pooler-host>;Port=6543;Database=postgres;Username=app_api_user.<staging-project-ref>;Password=<from password manager>;SSL Mode=Require
Supabase__Url=https://<staging-project-ref>.supabase.co
# Only if the staging project still uses the legacy shared JWT secret:
# Supabase__JwtSecret=<from password manager>
```

`Supabase__Url` must be the **staging** project. Tokens from any other project are rejected (401).

**Shell setup.** Paste tokens without echoing them or saving them in history:

```bash
API=https://<staging-api-host>
SB=https://<staging-project-ref>.supabase.co
APIKEY=<staging anon/publishable key>     # public key, but still keep it out of logs
read -rs TOKEN                            # paste an access token, press Enter
```

## Getting an access token without the frontend

Open this URL in a private browser window:

```
$SB/auth/v1/authorize?provider=<google|apple>&redirect_to=http://localhost:3000
```

After you sign in, the browser lands on `http://localhost:3000/#access_token=<…>&…`. Nothing needs to be running there, because the token is in the address bar. Copy only the `access_token` value into `read -rs TOKEN`, then close the window. Access tokens expire after about 1 hour.

## 1. Negative checks

```bash
curl -s -o /dev/null -w '%{http_code}\n' "$API/api/v1/me"                                    # 401
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer not-a-token" "$API/api/v1/me"  # 401
curl -s -o /dev/null -w '%{http_code}\n' "$API/api/v1/teams"                                  # 200, public
```

A token from a different Supabase project, such as production, must also give `401`.

## 2. Google sign-in

1. Get a token with `provider=google` using a Google test account.
2. Call the API twice:

```bash
curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/me"
curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/me"
```

Expected (`200`), the same both times:

```json
{ "id": 123, "username": "user_<32 hex chars>", "email": "<google address>", "authUserId": "<supabase user uuid>" }
```

- `authUserId` equals the user's id in Supabase → Authentication → Users.
- Record `id` as **GOOGLE_ID**.

## 3. Apple sign-in

1. Use a *different* Apple ID that has never signed in, get a token with `provider=apple`, and choose **Hide My Email** when Apple asks.
2. Call `/api/v1/me` as above.

Expected `200`, with `email` set to `<something>@privaterelay.appleid.com` and an `id` that is not GOOGLE_ID. Calling it again returns the same `id`.

## 4. Account linking keeps the profile

This uses the Google account from step 2. Start with a fresh Google token in `$TOKEN`.

1. Before linking:

   ```bash
   curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/me"     # note id → BEFORE_ID (= GOOGLE_ID)
   ```

2. Ask Supabase for an Apple linking URL as the signed-in user. This is what `supabase.auth.linkIdentity({ provider: 'apple' })` does:

   ```bash
   curl -s -H "Authorization: Bearer $TOKEN" -H "apikey: $APIKEY" \
     "$SB/auth/v1/user/identities/authorize?provider=apple&redirect_to=http://localhost:3000&skip_http_redirect=true"
   # → {"url":"https://appleid.apple.com/auth/authorize?..."}
   ```

   Open that `url` in the browser, sign in with an Apple ID **not** already used in step 3, and approve.

3. Confirm that Supabase now has both identities on one user:

   ```bash
   curl -s -H "Authorization: Bearer $TOKEN" -H "apikey: $APIKEY" "$SB/auth/v1/user" | jq '{id, providers: [.identities[].provider]}'
   # → {"id": "<same supabase uuid>", "providers": ["google", "apple"]}
   ```

4. Sign out, then sign in again **with Apple** (`provider=apple`), take the new token, and call:

   ```bash
   read -rs TOKEN
   curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/me"     # note id → AFTER_ID
   ```

**Pass:** `AFTER_ID == BEFORE_ID`, `authUserId` is unchanged, and any fantasy teams created before linking are still listed under that id:

```sql
SELECT id, user_id FROM app.fantasy_teams WHERE user_id = <BEFORE_ID>;
```

`email` stays as the Google address. The API keys profiles on the Supabase user id, not the email in the token.

**Fail:** a new `id` means Apple created a separate Supabase user and was not linked. Check that manual linking is enabled, and that you used `/user/identities/authorize` with the Google session rather than a plain Apple sign-in.

## 5. Legacy email conflict (optional)

This applies if staging has a pre-Supabase `app.users` row with `auth_user_id IS NULL`. Signing in with a provider whose email matches that row, in any letter case, must return:

```json
{ "status": 409, "title": "Account conflict", "code": "legacy_email_unlinked", "detail": "..." }
```

No new row is created and the legacy row is unchanged. To resolve it, an admin sets `auth_user_id` on the legacy row with an approved data fix (owner account, Supabase SQL Editor), and the fan retries.

## Results log

| Check | Date | Tester | Result |
|---|---|---|---|
| Negative checks | | | |
| Google sign-in | | | |
| Apple sign-in (relay email) | | | |
| Linking: BEFORE_ID == AFTER_ID | | | |
