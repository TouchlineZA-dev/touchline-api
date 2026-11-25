# Wizard contract: how the app drives Touchline step by step

**For:** Max and Funi (UI).

**Base URL:** `https://touchline-api-production-ed283561.up.railway.app/api/v1`. Every path below is relative to it.

**Signed in:** send `Authorization: Bearer <Supabase access token>` on every call except the public football reads.

This page covers:
- every place a manager types or picks something;
- for each step: which call checks it, which call saves it, which errors can come back, and what to show;
- drafts, sessions, and safe saving (If-Match, Idempotency-Key).

All the error codes are listed in [docs/api/errors.md](../api/errors.md).

## The three building blocks

### 1. Check without saving: the `validate` calls

While the manager edits, send what they have **so far**. You get live progress back and **nothing is saved**:

| What's being edited | Check with |
|---|---|
| The first squad (onboarding) | `POST /fantasy/team/squad/validate` (works before the team exists) |
| Lineup and substitutions | `POST /fantasy/team/lineup/validate` |
| A transfer plan | `POST /fantasy/team/transfers/validate` (Out and In can differ in length while choosing) |

Every check returns the same shape:

```json
{
  "kind": "squad",
  "complete": false,
  "valid": false,
  "budget": { "total": 100.0, "spent": 33.5, "left": 66.5, "overBudget": false },
  "positions": [
    { "position": "GK",  "have": 2, "need": 2 },
    { "position": "DEF", "have": 4, "need": 5 },
    { "position": "MID", "have": 0, "need": 5 },
    { "position": "ATT", "have": 1, "need": 3 }
  ],
  "clubs": [ { "clubId": 12, "club": "Orlando Pirates", "count": 3, "max": 3, "overLimit": false } ],
  "formation": null,
  "lineup": null,
  "captaincy": null,
  "transfers": null,
  "violations": [],
  "warnings": [],
  "nextSteps": ["Pick 1 more defender.", "Pick 5 more midfielders.", "Pick 2 more attackers."],
  "gameweekId": 10,
  "serverTimeUtc": "2026-11-25T18:00:00+00:00"
}
```

**How to show it**
- **Header:** "4/5 DEF · R66.5m left". Use `positions[].have/need` and `budget.left`.
- **Club warning:** if a club's `count == max`, show "Max 3 from Orlando Pirates", and disable more players from that club.
- **Problems:** `violations` lists rules already broken by what was sent, each with a `rule` name, for example `club_limit` or `over_budget`. Show `message` next to the related control.
- **Next steps:** show `nextSteps` as hints.
- **Save button:** enable it only when `valid` is true. `complete` means everything is filled in; `valid` means it's complete and no rule is broken.

**Rules for using the checks**
- **Debounce:** call the check about 300 ms after the last change. The limit is 120 checks a minute per manager.
- **A check is not a promise:** prices and the deadline are checked again on save. Always handle the save's own errors.

### 2. Drafts: save and resume unfinished work

One draft per manager per kind: `onboarding`, `squad`, `lineup`, `transfers`, `league`. A draft is only for the app's convenience. The server never treats it as a saved squad or team, and never scores it.

| Action | Call |
|---|---|
| Autosave (overwrite) | `PUT /me/drafts/{kind}` with `{ "payload": { … }, "version": <last version you got, 0 for new>, "step": "pick_defenders" }` |
| Load | `GET /me/drafts/{kind}` (404 `draft_not_found` if none) |
| List mine (no payloads) | `GET /me/drafts` |
| Discard | `DELETE /me/drafts/{kind}` (always 204) |

```http
PUT /me/drafts/squad
{ "payload": { "players": [101, 102, 201] }, "version": 3, "step": "pick_defenders" }
```

```json
{ "kind": "squad", "step": "pick_defenders", "payload": { "players": [101, 102, 201] }, "version": 4,
  "createdAtUtc": "2026-11-25T17:40:00+00:00", "updatedAtUtc": "2026-11-25T18:00:00+00:00", "expiresAtUtc": "2026-12-09T18:00:00+00:00" }
```

**Rules**
- **Version:** keep the `version` from each response and send it on the next save. A brand-new draft is `version: 0`.
- **Autosave:** about 2 seconds after the last change. The limit is 60 saves a minute per manager.
- **Payload:** a JSON object, under 64 KB. Store ids and choices, not whole player objects.
- **Expiry:** drafts expire after 14 days without a save.
- **Clean up:** after the real save succeeds (squad saved, transfer confirmed, league created), call `DELETE /me/drafts/{kind}`.
- **Two devices:** if another device saved first, you get **409 `draft_stale`** with `current`, the newer draft. If the other device discarded it, `current` is `{ "kind", "draft": null }`. Ask the manager:
  - "This was changed on another device. Use that version, or keep mine?"
  - **Keep mine:** save again with `version: current.version`, or with 0 if `draft` is null.
  - **Use that version:** load `current`.
- **Before the database is ready:** until migration 015 is applied, every draft call is **503 `drafts_unavailable`**. Keep drafts on the device, and don't show an error: everything else works.

**When the manager leaves a screen with unsaved changes**, offer three buttons:
- **Save draft:** `PUT` now, then leave.
- **Discard:** `DELETE /me/drafts/{kind}`, then leave.
- **Keep editing:** stay.

**When the manager comes back**, `GET /me` tells you:

```json
"onboarding": { "needsUsername": false, "needsTeam": false, "needsSquad": true, "step": "squad", "hasDraft": true, "drafts": ["squad"] }
```

If `hasDraft` is true, ask "Resume your draft?":
- **Yes:** `GET /me/drafts/{kind}`, then reopen the wizard at the draft's `step`.
- **No:** `DELETE /me/drafts/{kind}`.

### 3. Saving safely

- **Target gameweek:** every squad, lineup, transfer and chip save names `gameweekId`. Take it from `GET /fantasy/team/squad` or `/snapshot` (`gameweek.id`).
- **If-Match (lineup and the legacy one-step transfer):** read the `ETag` header from `GET /fantasy/team/squad` and send it back as `If-Match`. If the team changed on another device, you get **409 `stale_squad`** and `current` holds the latest squad.
- **Idempotency-Key (transfer confirm):** create one UUID per confirmation, and reuse the **same** key if you retry the same confirmation. A retry returns the original receipt (`replayed: true`) and never charges twice. A new confirmation needs a new key; reusing a key for different transfers is 409 `idempotency_key_reused`.
- **On any 409:**
  1. Reload what the response is about (`current` usually has it).
  2. Show what changed: "Your team was changed on another device", "Prices changed: new total R…".
  3. Let the manager redo or confirm.
  Never retry a 409 blindly.

## Sessions: never lose the manager's work

| Response | Meaning | What the app does |
|---|---|---|
| 401 `token_expired` (`retry: "refresh"`) | The session timed out | Refresh the Supabase session **silently**, retry the same request **once**, and keep the form as it is |
| 401 `token_missing` or `token_invalid` (`retry: "sign_in"`) | No usable session | Save the form as a draft (or on the device), sign in, then come back to it |
| 403 `account_deleted` | The account is gone | Sign out |
| 403 `account_suspended` | Reads only | Show the notice; keep the app usable for reading |
| 429 `rate_limited` | Too fast | Wait a few seconds and retry. **Not a session problem** |
| 400 / 404 / 409 / 413 / 5xx | The request or the data | Show the message and keep the form. **Never sign out** |

If the refresh fails, or the retry gives 401 again, treat it as "sign in again", but keep the draft. The session rules are the same in every wizard below.

```json
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer error="invalid_token", error_description="The access token expired"

{ "title": "Session expired", "status": 401, "code": "token_expired", "retry": "refresh",
  "detail": "The access token has expired. Refresh the Supabase session and retry the request." }
```

## The wizards

### Onboarding: username → favourite club (optional) → team name → squad

`GET /me` tells you where to start: `onboarding.step` is `username`, `team`, `squad` or `done`. Autosave the whole flow as the `onboarding` draft, and the squad step as the `squad` draft.

| Step | Check / save | Errors | What to show |
|---|---|---|---|
| Username | Save: `PATCH /me { "username": "Soweto_Sky" }`. Rules: 3–20 characters, letters, digits and underscore; unique ignoring case | 400 `rule_violation` with rule `username_length`, `username_characters` or `username_reserved`; 409 `username_taken` | Inline under the field: "That name is taken." The API never says by whom |
| Favourite club (skippable) | List: `GET /clubs`. Save: `PATCH /me { "favouriteClubId": 12 }`. Clear: `{ "clearFavouriteClub": true }` | 400 `favourite_club_unknown` | A "Skip" button. It never blocks onboarding |
| Team name | Save: `POST /fantasy/team { "name": "Soweto XI" }` (3–30 characters) | 400 `team_name_length`; 409 `team_exists` (load the team instead) | Inline under the field |
| Pick 15 players | Check: `POST /fantasy/team/squad/validate { "players": [ … ] }` after each pick. Players: `GET /players` (`available: true` only) | Rules: `position_count`, `club_limit`, `over_budget`, `duplicate_player`, `player_unavailable` | Progress header ("4/5 DEF · R66.5m left"); grey out full positions and clubs at 3 |
| Starting XI, bench, captain | Check: `POST /fantasy/team/squad/validate` with `formation`, `starters`, `bench`, `captainId`, `viceCaptainId` | `formation_not_allowed`, `formation_range`, `starting_gk`, `bench_gk`, `captain_vice_same`, `captain_not_starter` | Formation picker (4-3-3, 4-4-2, 3-5-2, 3-4-3, 5-3-2); `formation.actual` shows what the XI adds up to |
| Save the squad | `PUT /fantasy/team/squad` with the same body plus `gameweekId` | 400 (same rules); 409 `squad_already_picked` (load it); 409 `gameweek_locked` (use `current.nextGameweek.id`) | On success: `DELETE /me/drafts/squad` and `DELETE /me/drafts/onboarding` |

"Pick for me": `POST /fantasy/team/autopick/preview { "formation": "4-4-2" }` returns a legal squad to show. It is never saved; the manager saves it with `PUT /fantasy/team/squad`.

### Lineup and substitutions (draft kind `lineup`)

| Step | Check / save | Errors | What to show |
|---|---|---|---|
| Load | `GET /fantasy/team/squad`; keep its `ETag` | 404 `not_found` (no team, so go to onboarding) | Pitch view and bench order |
| Swap and move | Check: `POST /fantasy/team/lineup/validate` with the whole proposed lineup | `bench_gk_swap` (the bench GK only swaps with the GK), `formation_range`, `captain_not_starter`, `lineup_changes_players` | Block invalid drops; show `message` |
| Save | `PUT /fantasy/team/lineup` with `If-Match: <ETag>` and `gameweekId` | 409 `stale_squad` (reload `current`); 409 `gameweek_locked` | "Saved for Gameweek 12 (deadline Sat 15:30)" |

### Transfers (draft kind `transfers`)

| Step | Check / save | Errors | What to show |
|---|---|---|---|
| Choose who to sell and buy | `POST /fantasy/team/transfers/validate { "out": [203], "in": [] }` | `transfer_position_mismatch` (like for like), `club_limit`, `insufficient_funds`, `player_unavailable` | Bank after, "Buy 1 defender", free transfers left, hit (−4 each extra). `transfers.unlimited` is true in GW1, with Wildcard, and in a late entrant's first week |
| Quote | `POST /fantasy/team/transfers/preview { "gameweekId": 10, "out": [203], "in": [377] }` | 409 `gameweek_locked` | The quote: sale, purchase, bank after, hit. It's valid for 15 minutes (`expiresAtUtc`) |
| Confirm | `POST /fantasy/team/transfers/confirm` with the quote's `gameweekId`, `out`, `in`, `squadVersion`, `priceRevision` and `quotedAtUtc`, plus the header `Idempotency-Key: <uuid>` | 409 `price_changed` or `quote_expired` (show `current`, the new quote, and confirm again with a new key); 409 `stale_squad`; 400 `insufficient_funds` | The receipt. Then `DELETE /me/drafts/transfers` |

```http
POST /fantasy/team/transfers/confirm
Idempotency-Key: 00000000-0000-4000-8000-000000000001   (example: a new random UUID per confirmation)

{ "gameweekId": 10, "out": [203], "in": [377], "squadVersion": 7, "priceRevision": 41, "quotedAtUtc": "2026-11-25T18:00:00Z" }
```

### Chips

| Step | Call | Errors | What to show |
|---|---|---|---|
| What's available | `GET /fantasy/team/chips` | (none) | Wildcard, Bench Boost and Triple Captain (×4), each once a season, one per gameweek. There is no Free Hit |
| Play | `POST /fantasy/team/chips { "chip": "bench_boost", "gameweekId": 10 }` | `chip_used`, `chip_one_per_gameweek`, `chip_unknown`; 409 `gameweek_locked` | Confirm dialog first |
| Cancel | `DELETE /fantasy/team/chips/{chip}?gameweekId=10` (Bench Boost and Triple Captain, before the deadline) | 409 `chip_not_cancellable` (Wildcard, or the chip isn't active) | "Wildcard can't be cancelled once played" |

### Leagues (draft kind `league`)

| Step | Call | Errors | What to show |
|---|---|---|---|
| Create | `POST /fantasy/leagues { "name": "Office league" }` (1–50 characters) | `league_name_length` | Show and share `joinCode` (for example `TL-482#91!3`) |
| Join | `POST /fantasy/leagues/join { "code": "TL-482#91!3" }` | 400 `league_code_format`; 404 `not_found`; 409 `league_full` (20 teams); 409 `already_member` | Validate the shape as the manager types: `TL-`, 6 digits, 2 of `# ! @ -` |
| Leave | `POST /fantasy/leagues/{id}/leave` | 400 `owner_cannot_leave` | Owners hand over first: `POST /fantasy/leagues/{id}/owner` |
| Standings | `GET /fantasy/leagues/{id}/standings[?gameweek=N]`; `overall` for everyone | 403 `forbidden` (not a member) | Points match the points page (same scoring) |

### Account settings

| Setting | Call | Errors |
|---|---|---|
| Username | `PATCH /me { "username": … }` | `username_taken`, `username_cooldown` (one change every 30 days after the first) |
| Favourite club | `PATCH /me { "favouriteClubId": … }` or `{ "clearFavouriteClub": true }` | `favourite_club_unknown` |
| Team name | `PATCH /fantasy/team { "name": … }` | `team_name_length` |
| Notifications | `GET` / `PATCH /me/notifications` | (none) |
| Export my data | `GET /me/export` | 403 `reauthentication_required` (sign in again within 10 minutes, then retry) |
| Delete account | `DELETE /me` | 403 `reauthentication_required` |

## Deadlines

- **Where to find them:** `GET /gameweeks?seasonId=…` (public) gives `deadline` (UTC) and `deadlineSast` for each week. Signed-in responses carry `gameweek.deadline`.
- **Countdown:** compare against `serverTimeUtc` from any response, not the phone's clock.
- **After the deadline:** saves for that week are refused with 409 `gameweek_locked`. `current.nextGameweek` says where changes go now, so offer "Apply to Gameweek 13 instead?" Nothing is ever moved to the next week silently.
