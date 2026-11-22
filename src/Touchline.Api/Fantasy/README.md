# Fantasy rules

The game rules live in this folder as plain C# with no database access, so each rule can be unit-tested on its own (`tests/Touchline.Api.Tests/Fantasy`). Database work is in `Services/`, routes in `Controllers/`.

| File | What it holds |
|---|---|
| `FantasyRules.cs` | Every rule number, bound from the `Fantasy` section of appsettings |
| `FantasyModels.cs` | Formation, player, squad selection, chips, rule-check result |
| `SquadRules.cs` | Squad, starting XI, bench, captaincy and transfer validation |
| `TransferAndChipRules.cs` | Free transfers, points hits, chips |
| `Scoring.cs` | Minutes, player points per fixture and gameweek, team points, captain auto-pick |
| `GameweekRules.cs` | Gameweek membership, deadlines, live minute, finalising, leagues, join codes, If-Match versions |
| `PlayerValuation.cs` | The price formula and the backtest |

Money is in millions of rand with one decimal (7.5 = R7.5m). The API returns the number; the app shows "R7.5m".

## Rules

### Teams and squads
- One team per user per season. Creating a team needs only a name. Budget **R100.0m**; the squad's cost must be at most the budget, and `budget_remaining` is kept up to date.
- **15 players: 2 GK, 5 DEF, 5 MID, 3 ATT**, each player once. Position comes from `football.players.position_id` (24 GK, 25 DEF, 26 MID, 27 ATT) and can't be changed. A player with an unknown position can't be picked.
- **Max 3 players from one real club.** A player's club is the team he most recently lined up for this season. A player with no club yet is allowed and flagged in the response.
- **Starting XI:** exactly 1 GK plus a formation from 4-3-3, 4-4-2, 3-5-2, 3-4-3, 5-3-2 (so 3–5 DEF, 3–5 MID, 2–3 ATT).
- **Bench:** 4 players, exactly 1 GK. The bench GK can only swap with the starting GK.
- **Captain and vice-captain:** different players, both starting. A goalkeeper can be captain. If a team reaches the deadline with no captain or vice-captain, one is picked at random from its starting XI.
- Changes lock at the **deadline: 90 minutes before the gameweek's first kickoff**. A change the app made offline for a gameweek that has since locked is rejected with a message saying so.
- Every check runs on every save, and every broken rule is returned at once.

### Transfers and chips
- **1 free transfer per gameweek**, no rollover. Each extra transfer costs **4 points**. Unlimited free transfers before the Gameweek 1 deadline and while Wildcard is active (playing Wildcard also clears hits already taken that gameweek).
- Transfers swap like for like (a GK only for a GK, and so on). The incoming player takes the outgoing player's spot and armband. Selling returns the player's **current** price; price changes move team value but never `budget_remaining`.
- **Bench Boost:** bench points count. **Triple Captain:** captain ×4. **Wildcard:** unlimited free transfers. Each chip once per season, at most one per gameweek.

### Scoring (per player, per fixture, added up over the gameweek)
- **Minutes** (our plan has none, so they're worked out): from 0 if he started or the minute he came on, until the minute he was subbed off or sent off, else the end of the match (fixture length, or 90; for live matches, the current minute).
- **Appearance:** 45+ minutes = 1; on the pitch from kickoff to the final whistle = 2 in total.
- **Goal** (types 14 and 16) +3. **Assist** (the goal event's related player) +2, switchable with `AssistsEnabled`. Rescinded events are ignored.
- **Clean sheet** +2 for GK and DEF with 60+ minutes whose team conceded 0 in that fixture.
- **Yellow** −1, **red** −2, **second yellow** −2 in total (no extra −1), **own goal** −2.

### Team points
- Starters count. **No auto-subs:** a starter who didn't play scores 0. The bench counts only with Bench Boost.
- **Captain ×3, vice-captain ×2.** If the captain plays 0 minutes, the vice-captain gets the armband (×3, or ×4 with Triple Captain).
- Then transfer hits are subtracted.

### Live and final points
- While a gameweek is being played, points and standings are **provisional**: worked out from the latest football data, never saved, cached in memory for 60 seconds, and marked `"provisional": true` with each fixture's status.
- A live match's current minute comes from the clock: time since kickoff, minus 15 minutes of half-time once past 45, capped at the match length, and never behind the latest event.
- A gameweek is **finalised** (saved to `app.points_results` with a JSON breakdown, team totals in `app.fantasy_gameweek_entries`) only when `sync.gameweek_sync_status.marked_complete` is true or every fixture is final (state 5, 7 or 8). Re-running it updates rows; it never duplicates them.

### Leagues
- Private only: join by exact code, no public list. Any user with a team can create one; the owner's team joins automatically and the owner can't leave. Max **20 teams** including the owner, enforced under a row lock. Leaving sets `left_at`.
- Join codes: `TL-` then 6 random digits with 2 special characters from `# ! @ -` placed between digits, never next to each other, e.g. `TL-482#91!3`. Codes are unique (regenerated if taken). Only the owner sees the code.
- **Overall** is every team in the season, worked out with no table row.
- Standings: season total, or one gameweek with `?gameweek=N`. Ties go to overall points, then username. Live provisional points are included.

### Offline support
- `GET /fantasy/team/snapshot` returns everything to show the team offline in one call.
- GET fantasy endpoints send an `ETag` and `Cache-Control: private, no-cache`, and answer `304 Not Modified` to a matching `If-None-Match`.
- `PUT /lineup` and `POST /transfers` accept `If-Match` with the ETag from `GET /squad` or `/snapshot` (`"v{version}-{hash}"`). A stale version gets **409** with the current team in `current`.

## Player prices

Our own formula, not the Premier League's. Maths in `PlayerValuation.cs`, data loading in `Services/PricingService.cs`.

1. **When:** once per PSL transfer window (pre-season, mid-season), when an admin runs `POST /admin/prices/recalculate?windowId=N`. A second run for the same window needs `&force=true`. A window that opens before the Gameweek 1 deadline is the pre-season window; that run must happen before the Gameweek 1 deadline. Mid-season prices apply from the first gameweek whose deadline is after the window opens.
2. **Points per 90 for each past season:** `R = points ÷ minutes × 90`, with the scoring rules above (base points only), PSL league matches only. 0 minutes = no R.
3. **Small-sample safeguard:** `R_adj = (minutes × R + K × R_pos_avg) ÷ (minutes + K)`, K = 900, `R_pos_avg` = minutes-weighted average R of that position that season.
4. **Expected points:** `E = (0.6 × R1 + 0.3 × R2 + 0.1 × R3) × M ÷ 90`. Missing seasons drop out and the weights rescale to add up to 1. M = last season's minutes, capped at league matches × 90 (or an admin override). Mid-season: R1 is this season so far, and M = minutes per match available × matches left (or last season's M scaled to matches left under 450 minutes). "Matches available" = his club's league matches since his first matchday squad this season.
5. **Price:** within each position, `Q = (rank − 1) ÷ (n − 1)` (rank 1 = lowest E, ties share the lower rank, n = 1 → 0.5); `V = 0.5 × round((Vmin + (Vmax − Vmin) × Q) ÷ 0.5)`, halves up, in decimal.
6. **No history** in the last 3 seasons → position minimum, flagged "fallback price used".
7. **Overrides** (`app.player_price_overrides`, reason required): a price override wins; an expected-minutes override replaces M.
8. **Saving:** `app.player_prices` updated, one `app.player_price_changes` row per change, all in one transaction.
9. **Preview:** `POST /admin/prices/preview?seasonId=N` prices season N as a pre-season run would, saves nothing, and for a season with results adds a backtest per position (average real points per R1.0m price band, and the price–points correlation). It also reports how many PSL seasons the database holds.

Worked example (unit-tested): a midfielder with R1 = 6, R2 = 5, R3 = 4 and M = 2400 has E = 146.7; at Q = 0.90 the price is 11.25 → **R11.5m**.

## Settings (`Fantasy` section)

| Setting | Default | Meaning |
|---|---|---|
| `Budget` | 100.0 | Starting budget (millions) |
| `SquadGoalkeepers` / `SquadDefenders` / `SquadMidfielders` / `SquadAttackers` | 2 / 5 / 5 / 3 | Squad composition |
| `MaxPlayersPerClub` | 3 | Per real club |
| `Formations` | `4-3-3,4-4-2,3-5-2,3-4-3,5-3-2` | Allowed formations (comma-separated) |
| `DeadlineMinutesBeforeFirstKickoff` | 90 | Deadline |
| `FreeTransfersPerGameweek` | 1 | No rollover |
| `TransferHitPoints` | 4 | Per extra transfer |
| `CaptainMultiplier` / `ViceCaptainMultiplier` / `TripleCaptainMultiplier` | 3 / 2 / 4 | |
| `AppearanceMinMinutes` / `AppearancePoints` / `FullMatchPoints` | 45 / 1 / 2 | |
| `GoalPoints` / `AssistPoints` / `AssistsEnabled` | 3 / 2 / true | |
| `CleanSheetPoints` / `CleanSheetMinMinutes` | 2 / 60 | GK and DEF only |
| `YellowCardPoints` / `RedCardPoints` / `OwnGoalPoints` | −1 / −2 / −2 | |
| `DefaultMatchLengthMinutes` / `HalfTimeBreakMinutes` | 90 / 15 | Minutes and live clock |
| `LivePointsCacheSeconds` | 60 | Provisional points cache |
| `LeagueMaxMembers` | 20 | Including the owner |
| `JoinCodePrefix` / `JoinCodeDigits` / `JoinCodeSpecialCount` / `JoinCodeSpecialChars` / `JoinCodeMaxAttempts` | `TL-` / 6 / 2 / `#!@-` / 20 | |
| `Pricing:CompetitionSportmonksId` | 806 | PSL league matches only |
| `Pricing:ShrinkMinutes` | 900 | K |
| `Pricing:WeightLastSeason` / `WeightTwoSeasonsAgo` / `WeightThreeSeasonsAgo` | 0.6 / 0.3 / 0.1 | |
| `Pricing:MidSeasonMinMinutes` | 450 | |
| `Pricing:PriceStep` | 0.5 | Rounding step |
| `Pricing:Goalkeeper` / `Defender` / `Midfielder` / `Attacker` (`Min`, `Max`) | 4.0–6.0 / 4.0–7.0 / 4.5–12.0 / 4.5–12.5 | Price ranges |

## Not ready yet

- **Sportmonks codes:** which `player_lineups.type_id` is the starting XI and which player in a substitution comes on are not confirmed against the database. They're null in `Business/SportmonksCodes.cs`, which includes the SQL to confirm them. Until they're set, scoring, live points and pricing return 503; squads, transfers, chips and leagues work.
- **Schema:** the fantasy endpoints need proposed migrations V033 and V034 (`docs/proposed-migrations`). Until then they return 503 "Schema not ready".
