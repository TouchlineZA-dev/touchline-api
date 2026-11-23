# Touchline fantasy rulebook: 2026/27, version `2026-27.v1`

**Status and version**
- **Draft for product sign-off.** Leruo and Phatu sign it off (see "Sign-off" at the end).
- **Points table version:** `2026-27.points.v1`.
- **Machine-readable copy:** `GET /api/v1/rules` (public, no sign-in). It is generated from the same settings the API scores with (`Fantasy` section of `appsettings.json`), so the published rules and the applied rules can't drift apart.

**Versioning**
- Every scored gameweek entry and points row records the rules version and points-table version it was scored under (migration 008 / V044).
- A new version never rescores the past: finalising skips any entry already scored under another version.
- Changing a rule means publishing `2026-27.v2` (or a new season's v1) before the gameweek it first applies to.

## 1. Money

- **Units:** money is in millions of rand with one decimal place. 7.5 means R7.5m.
- **Arithmetic:** `decimal` on the server and `NUMERIC(5,1)` in the database. Never floating point.
- **Budget:** R100.0m.
- **Prices:** set once per transfer window by the agreed valuation formula (`Fantasy/PlayerValuation.cs`).
  - Each position has its own range.
  - Prices are rounded to steps of R0.5m, halves rounded up.
- **Buying:** a purchase keeps the price paid on the squad row.
- **Selling:** a player sells at his **current** price. There's no profit sharing and no sell-on rounding beyond the R0.5m price step.

## 2. Squad and lineup

| Rule | Value |
|---|---|
| Squad | 15 players: 2 GK, 5 DEF, 5 MID, 3 ATT |
| Club cap | at most 3 players from one real club |
| Starting XI | exactly 1 GK; formation 4-3-3, 4-4-2, 3-5-2, 3-4-3 or 5-3-2 (so at least 3 DEF) |
| Bench | 1 GK + 3 outfield, in order; the bench GK only swaps with the GK |
| Pickable | players who have been in a current-season PSL matchday squad (lineup type 11 or 12), unless an admin override marks them available or unavailable |

## 3. Deadline

- **Timing:** each gameweek locks 90 minutes before its first kickoff.
- **Publication:** the exact instant is published in UTC (`deadline`) and in South African time (`deadlineSast`, UTC+02:00, no daylight saving) on every gameweek in API responses.
- **Protecting history:** a deadline that has passed **never changes**, so locked entries and published history stay as they were.
- **When a future deadline may change:** only before it passes, through an admin edit with a reason (`PUT /api/v1/admin/gameweeks/{id}/deadline`).
  - Typical cause: the first kickoff moved.
  - The new deadline must still be in the future and no later than the first kickoff.
- **How managers are told:** every gameweek response carries the current deadline. Push reminders arrive with notification preferences (Sprint 3) and the delivery work (Sprint 6).
- **Lineup timing:** lineups arrive about 30 minutes before kickoff, after the deadline. That's expected. Managers pick from "has appeared in a current-season lineup".

## 4. Transfers

- **Allowance:** 1 free transfer per gameweek. Unused transfers don't roll over.
- **Hits:** each extra transfer costs 4 points from that gameweek's total.
- **Like for like:** a goalkeeper is replaced by a goalkeeper, and so on.
- **Unlimited free transfers:**
  - before the Gameweek 1 deadline;
  - in the gameweek Wildcard is active (including transfers made earlier that week);
  - before a late entrant's first deadline (§5).

## 5. Late entry

- A team created after Gameweek 1 starts in the next gameweek whose deadline hasn't passed.
- It has unlimited free transfers before that first deadline.
- It gets no points for earlier gameweeks: it has no entry for them.
- It joins Overall and private leagues with its points from its first gameweek onwards.

## 6. Captaincy

- **Multipliers:** captain ×3, vice-captain ×2.
- **Vice taking the armband:** the vice-captain gets the captain's multiplier only if the captain plays **0 minutes across all of the gameweek's fixtures**. In a double week, a captain who plays only the second fixture keeps the armband.
- **Missing armband:** a missing captain or vice-captain is filled at scoring by a deterministic pick from the starting XI. The pick is seeded by team and gameweek, so provisional points, finalising and re-scoring always pick the same player.

## 7. Chips

| Chip | Effect |
|---|---|
| Triple Captain | the captain scores ×4; passes to the vice-captain (×4) if the captain plays 0 minutes |
| Bench Boost | the four bench players' points count |
| Wildcard | unlimited free transfers for the gameweek, including ones already made that week |

- Each chip can be used **once per season**, with at most **one chip per gameweek**.
- There is one chip period: the whole season, with no halves.
- **No Free Hit.**
- **Activation and cancellation:** Sprint 4.

## 8. No automatic substitutions

A starter who doesn't play scores 0. Bench players count only with Bench Boost.

## 9. Scoring (points table `2026-27.points.v1`)

| Event | Points |
|---|---|
| Appearance, 45 or more minutes | +1 |
| Appearance, full match (started, on the pitch at the final whistle of a finished match) | +2 (in total) |
| Goal (event 14 or penalty goal 16) | +3 |
| Assist (the goal event's related player) | +2 |
| Clean sheet (GK or DEF, 60+ minutes, team conceded 0 in the match) | +2 |
| Yellow card | −1 |
| Red card | −2 |
| Second yellow (event 21) | −2 in total (counts as a red only; the yellow isn't deducted too) |
| Own goal (event 15) | −2 |

- **Not scored:** missed penalties, saves, defensive contributions, bonus points.
- **Unattributed events:** a goal or card with no identifiable player counts for the team's score (so for clean sheets) but gives nobody points.
- **Clean sheets:** use the team's goals conceded in the whole match, for players with 60+ minutes on the pitch.

## 10. Minutes

- **Inclusive counting:**
  - going off in minute m plays m minutes;
  - coming on in minute m plays (length − m + 1);
  - so a 46' substitution gives 46 to the player going off and 45 to the player coming on.
- **Red cards:** a red card in minute m plays m.
- **Stoppage time:** 45+x counts as 45 (a 45+x substitute plays from minute 46); 90+x counts as 90.
- **Cap:** never more than the match length (90 when the length is unknown).
- **Sources:** the starting XI (lineup type 11), substitutions (event 18: `player_id` is the player coming on) and red cards. Minutes in extra time and stoppage are approximate.

## 11. Double and blank weeks; fixture reassignment

- **Double and blank weeks:** a player scores in every fixture assigned to the gameweek, and the scores are added up. A gameweek with no fixture for his club scores 0 for him.
- **Assignment:** fixtures belong to the gameweek they are **assigned** to. The assignment is persisted with its history: when, why and who (migration 007 / V043).
- **Postponed fixtures** (default decision 1, open for sign-off):
  - a postponed fixture leaves its gameweek, which doesn't wait for it;
  - when the fixture is rescheduled, an admin assigns it to the gameweek containing the new kickoff, and points count there;
  - with no new date it stays unassigned.
- **Locked gameweeks:** moving a fixture into or out of a gameweek whose deadline has passed needs a recorded admin decision (`confirmLockedChange`). The result says whether a finalised gameweek needs a rescore.

## 12. Corrections

- **Live data:** provider corrections to live data (a re-attributed scorer, a rescinded goal) change provisional points straight away. Every view reads the same corrected rows, and the match's `revision` changes.
- **After finalising:** a correction is applied only by an **admin re-score**.
  - The re-score is audited (who, when, why) and publishes a new revision.
  - Past entries keep the rules version they were scored under.
- **Unattributed goals and cards** (default decision 4):
  - the fixture stays under review for a hold window (default 48 h) so a later sync can attribute it;
  - after that, an admin can finalise through a recorded manual resolution.
- **Implementation:** revisions, amendment publication, impact preview and the audit trail are delivered in Sprint 5.

## 13. Ties

| Table | Order |
|---|---|
| Private and Overall leagues | points; then overall points; then username (A–Z); then team id |
| Gameweek standings | gameweek points; then overall points; then username; then team id |
| PSL table | points; goal difference; goals for; club name |

## 14. Leagues

- **Joining:** private leagues are joined with an exact code only.
- **Code format:** `TL-` plus 6 digits, with 2 of `# ! @ -` between digits (never adjacent, first or last).
- **Size:** at most 20 teams, owner included. The owner can't leave.
- **Overall:** every team is in the Overall league.

## 15. Differences from FPL Classic 2026/27 (for product sign-off)

| Capability | FPL Classic | Touchline | Why |
|---|---|---|---|
| Entry | One entry in many leagues; details editable | One named team per manager per season; editable team name and username; late entry starts at the next open deadline | Same model; late-entry policy explicit |
| Selection | 15 players, £100m, 3 per club, legal formations, autopick | 15 players (2/5/5/3), R100.0m, 3 per club, five formations only | PSL budget; fixed formation list |
| Deadline | Before the gameweek deadline | 90 min before the first kickoff, published in UTC and SAST | Lineups arrive about 30 min before kickoff |
| Captain and bench | Captain ×2; vice if captain plays 0; automatic subs | Captain ×3, vice ×2 (×3 if the captain plays 0 all week); no automatic subs | Agreed Touchline rule |
| Transfers | 1 free a week, bank up to 5, −4 per extra | 1 free a week, no rollover, −4 per extra; unlimited before GW1, with Wildcard and before a late entrant's first deadline | Simpler allowance for launch |
| Market prices | Sale price shares profit; daily price changes | Sell at current price; prices change per transfer window by a published formula | PSL volume can't support daily demand pricing |
| Chips | Wildcard, Free Hit, Bench Boost, Triple Captain, each half | Wildcard, Bench Boost, Triple Captain (×4), once per season; no Free Hit | Agreed launch scope |
| Scoring | Position-specific goals and clean sheets, saves, penalties, defensive contributions, bonus | Flat table (§9) | Only categories with reliable player-level data (coverage matrix) |
| Rankings | Live ranks, final scores, history | Live provisional and final ranks from one revision; ties by overall points then username | Reproducible, explainable ranks |
| Social competition | Classic, general, H2H leagues and cups | Private classic leagues by code (max 20) plus Overall | H2H and cups later |
| Player tools | Watchlists, form, ownership, fixture difficulty, price indicators | Minutes, fixture history, upcoming opponents, points reasons first | Form and ownership once data supports them |
| Additional modes | Draft, Challenge | Season-long Classic only | Each mode needs its own contracts |

**Conflict for sign-off:** Phatu's handbook reference calculation uses FPL rules (captain ×2, autosubs) and gets **42** and **34**. Under the rules above, the same inputs give **44** and **41**. See `docs/sprints/sprint-2-report.md`, "Conflicts".

## Sign-off

| Role | Name | Decision | Date |
|---|---|---|---|
| Product | | | |
| Fantasy lead | Leruo | | |
| Second reviewer (reference week) | | | |
