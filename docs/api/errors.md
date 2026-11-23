# Error responses

**Format:** every error is an RFC 9110 problem-details body (`application/problem+json`):

```json
{ "type": "...", "title": "Prices changed", "status": 409, "detail": "Prices changed since this quote, so nothing was saved.",
  "code": "price_changed", "current": { ...new quote or current state... } }
```

**Status codes:** the existing statuses are unchanged:
- 400: validation;
- 401: not signed in or bad token;
- 403: not allowed;
- 404: missing (e.g. no team yet);
- 409: conflict;
- 503: data or schema not available.

**`code`:** machine-readable, so the app never parses English. It's present on every fantasy and account error. Rule violations also list every broken rule in `errors`.

## Codes

| Status | `code` | Meaning | What the app does |
|---|---|---|---|
| 400 | `rule_violation` | One or more fantasy rules are broken; `errors` lists them | Show the errors |
| 400 | `insufficient_funds` | The transfers cost more than the bank holds | Show the shortfall; suggest cheaper players |
| 400 | `target_gameweek_required` | A squad, lineup, transfer or chip write didn't say which gameweek it's for | Send `gameweekId` from `GET /fantasy/team/squad` or `/snapshot` |
| 400 | `idempotency_key_required` | A transfer confirmation came without an `Idempotency-Key` header | Generate a UUID per confirmation and resend |
| 401 | (none; the JWT handler answers) | Missing, expired or invalid token | Refresh the Supabase session and retry |
| 401 | `invalid_subject` | The token has no valid `sub` | Sign in again |
| 403 | `forbidden` | Signed in but not allowed (e.g. not an admin, not a league member) | Hide the action |
| 403 | `account_suspended` | Suspended account: reads only | Show the suspension notice |
| 403 | `account_deleted` | The account was deleted | Sign out |
| 403 | `reauthentication_required` | Export or deletion needs a sign-in within the last 10 minutes | Ask the fan to sign in again, then retry |
| 404 | `not_found` | E.g. no team for this season yet | Offer to create one |
| 409 | `stale_squad` | The team changed since the app loaded it (another device, or the version in the quote). `current` has the latest squad when available | Reload, show the latest team, let the fan redo the change |
| 409 | `gameweek_locked` | The target gameweek's deadline has passed; nothing was saved. `current.nextGameweek` says where changes go now | Move the edit to the next gameweek if the fan wants |
| 409 | `squad_already_picked` | A first squad pick when a squad exists (or another device just saved one) | Load the saved squad |
| 409 | `price_changed` | A price moved between preview and confirmation. `current` is a new quote | Show the new quote and confirm again |
| 409 | `quote_expired` | The quote is older than 15 minutes. `current` is a new quote | Show the new quote and confirm again |
| 409 | `idempotency_key_reused` | This key was already used for a **different** request | Use a new key; the original request's result stands |
| 409 | `username_taken` | Another manager holds the username (never says who) | Ask for another username |
| 409 | `chip_not_cancellable` | Wildcard can't be cancelled, or the chip isn't active that week | Explain |
| 409 | `legacy_email_unlinked`, `email_in_use` | Account email conflicts on first sign-in (see `docs/accounts/policies.md`) | Show the recovery text |
| 503 | (title "Schema not ready") | A migration the feature needs isn't applied yet | Retry later |
| 503 | (title "Sportmonks codes not confirmed") | Scoring decoding isn't enabled | Retry later |

## Writes, versions and retries

- **Target gameweek.** Every squad, lineup, transfer and chip write names `gameweekId`. The legacy `gameweek` number is still accepted. Responses carry `gameweek` (id, number, deadline in UTC and SAST, locked), `version` and `serverTimeUtc`. A request for a locked gameweek never edits the next one (`gameweek_locked`).
- **Versions.** `GET /fantasy/team/squad` and `/snapshot` send the squad version as the ETag (`"v{version}-…"`).
  - **Lineup and legacy transfer writes:** send it back in `If-Match`.
  - **Transfer confirmations:** carry the quote's `squadVersion`.
  - **How the check works:** writes to one team are serialised with a row lock, and the version is a concurrency token on the update. Two devices editing the same version: one wins, the other gets `409 stale_squad`.
- **Transfers.**
  1. `POST /fantasy/team/transfers/preview` returns a quote with `gameweekId`, `squadVersion`, `priceRevision`, sale proceeds, purchase cost, bank before and after, free transfers before and after, points hit, `valid` and `violations`, `quotedAtUtc` and `expiresAtUtc`.
  2. `POST /fantasy/team/transfers/confirm` takes the quote's values plus an `Idempotency-Key` header.
  3. Retrying with the same key and body returns the **original receipt** (`replayed: true`), even after a restart or the deadline. That's how a response lost after the commit is recovered.
- **Receipts.** Each accepted confirmation has one receipt, stored in `app.fantasy_receipts`, holding:
  - receipt id;
  - versions before and after;
  - each transfer with its prices;
  - money and hits;
  - the time.
