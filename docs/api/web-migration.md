# Moving the web app from the Netlify functions to the C# API

**For:** the web client owner.

**Base URL:** `VITE_API_BASE_URL` → the API's `/api/v1`. The paths match the old Netlify functions where they existed.

| Old Netlify function | New C# endpoint | Differences to handle |
|---|---|---|
| `/fixtures` | `GET /api/v1/fixtures` | `kickoffUtc` is the field. `kickoff` is still served, with the same UTC instant, **during the rollout only**: move to `kickoffUtc`, then `kickoff` will be removed. New fields: `phase`, `needsReview`, `competitionId`. Status may now be `Unknown` (an unrecognised provider code): add a default branch. A match that hasn't started has `homeScore`/`awayScore` `null`, not 0. New filters: `?date=` (SAST day), `?competitionId=`, `?teamId=`, optional `?page`/`?pageSize` |
| `/matches?fixtureId=` | `GET /api/v1/matches?fixtureId=`, the same as `GET /api/v1/fixtures/{id}` | One match-detail shape: `fixture`, `clock`, `events` (paged, with `display` such as `45+2`), `lineups`, `substitutions`, `stats` (team-level only), `coverage`, `freshness`, `revision`. Player ids are internal `football.players.id` |
| `/standings` | `GET /api/v1/standings` | Same rows, ordering and `form`. Adds `deduction`, `tieBreak`, `official` and `discrepancy`. Awarded results count. `computedFrom` says whether official deductions were applied |
| `/player-rankings` | `GET /api/v1/player-rankings` | Same filters (`competitionId`, `seasonId` accept internal or Sportmonks ids), top 10 per category, and tie rule (total, then name). Rescinded events are no longer counted |
| `/players` | `GET /api/v1/players`, plus `GET /api/v1/players/{id}` | Optional paging. A player detail route exists |
| `/news` | not in the C# API yet | See `docs/api/news-proposal.md` |

## Player ratings

The C# API doesn't serve player ratings, and won't invent them. The web's synthetic ratings should be **retired**. If a rating is wanted, define it separately (inputs, formula, version) and label it in the UI as "Touchline rating", not as a provider statistic. Until then, show minutes, goals, assists and fantasy points (`GET /api/v1/players/{id}`).

## Fantasy and account calls

- **Every write needs the target gameweek:** send `gameweekId`.
- **Every `409`/`400` has a `code`:** see `docs/api/errors.md`.
- **Transfers:** use preview, then confirm with an `Idempotency-Key`.
- **Account:** the profile is `GET/PATCH /api/v1/me`. Username replaces display name; the favourite club uses `football.teams.id` (`docs/accounts/club-mapping.md`).
- **Passwords:** stop storing passwords in `localStorage` (`authService.ts`). Supabase Auth holds credentials.
- **ETag:** CORS exposes `ETag` to allowed origins, so the browser can read the squad version and send it back in `If-Match`.
