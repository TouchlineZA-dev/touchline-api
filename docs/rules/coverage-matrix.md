# Scoring coverage matrix (handbook Gate 1), 2026/27

This is the evidence for each points category in rulebook `2026-27.v1`. It shows where the player-level input comes from and how missing data is handled.

**Sources:**
- the sync owner's Sprint 1 coverage sheet (touchline-sync `docs/sprint1/sportmonks-coverage.md`, 23 Nov 2026);
- runbook Part 1b;
- read-only production checks on 22–23 Nov.

**Rule:** a category is publishable only if Postgres holds an attributable **player-level** input. Team-level stats (`football.player_match_stats`, whose production `player_id` column is always NULL and is never read) can't award player points.

## What each state means

| State | Meaning | How the API tells it apart |
|---|---|---|
| Confirmed zero | The match is finished and settled, the input type is supplied by our plan, and there is no event for this player | The fixture is final (5/7/8) and the gameweek completeness evidence is present. Until the worker writes `sync.gameweek_sync_status` (0 rows today), the API **can't** confirm zero by itself, so the gameweek stays under review (Sprint 5) |
| Missing | The input should exist but doesn't (e.g. a goal with no scorer id) | Match detail `coverage.unattributedEvents`; lineup or event rows with `resolved: false` |
| Delayed | The match is finished but inside the worker's 4 h settle window, or the feed is stale | `freshness.stale`, match phase, and the gate not yet marked |
| Unsupported | Not ingested or not on our plan | Not a scoring category; `coverage.playerStats = "unsupported"` |

## Matrix

| Category | Player-level input in Postgres | Confirmed zero vs missing vs delayed vs unsupported | States that block settlement | What the API shows while waiting | Missing-data policy | Worked example | Test |
|---|---|---|---|---|---|---|---|
| **Minutes / appearance** | `player_lineups` type 11 (XI), 12 (bench); `match_events` 18 (sub: `player_id` on, `related_player_id` off), 20/21 (sent off) | Zero = on the bench, never subbed on, finished match. Missing = lineup row with no player id (296 this season) or a sub with no on-player (44 across all seasons). Delayed = no lineup yet (lineups arrive about 30 min before kickoff; `coverage.lineups = "pending"`) | Fixture not final; no lineup for a finished fixture (`coverage.lineups = "unavailable"`) | Provisional minutes from the live clock (`MatchClock`) | A player with no resolvable id gets no minutes; the team is unaffected. A lineup missing for a finished match keeps the week under review | Ndaba off 46' → 46 min (+1); Lungu on 46' → 45 min (+1) (GW9, fixture 1393) | `ReplayTests.Steps_0_to_3…`, `ScoringTests` |
| **Goal** | `match_events` 14 and 16, `sportmonks_player_id` | Zero = no goal event for him in a final match. Missing = goal with no scorer id (8 this season). Delayed = live or settle window | Unattributed goal: hold window 48 h, then admin manual resolution (default decision 4; Sprint 5) | Goal in the timeline with `player.resolved = false`; the score counts | Counts for the team score (and so clean sheets); nobody gets +3 | Matuludi 34', 66' → +6 (GW9, fixture 1389) | `Penalty_goal_16…`, `Unattributed_goal_and_card…` |
| **Assist** | Goal event's `sportmonks_related_player_id` (Part 1b confirms related = assist) | Zero = goal with no related player (common: e.g. Mmodi 30' in 1393). Missing can't be told apart from zero: the provider sometimes omits the assister. Delayed = live | None by itself | Assist in the timeline as `relatedRole: "assist"` | An absent assister means no assist points. This is the main residual risk; `Fantasy:AssistsEnabled` can switch assists off | Mokobodi assists 24' and 55' → +4 (1393, step 3); after step 4a the 24' assist goes with the re-attribution → +2 | `ReplayTests.Step_4a…` |
| **Clean sheet** | Team goals conceded (`fixtures.home_score/away_score`) plus the player's minutes (GK/DEF, 60+) | Zero conceded only from a final score. An unattributed goal still counts against it | Fixture not final | Provisional clean sheet while live (flagged provisional) | Uses the team score, never player stats | Majadibodu off 76', Chippa won 2–0 → +2 (1394) | `Unattributed_goal_and_card…` (conceded 1 → no clean sheet), `ScoringTests` |
| **Yellow card** | `match_events` 19 `sportmonks_player_id` | Missing = card with no player (75 of 1,136 across all seasons) | Unattributed card: hold window, then manual resolution (default decision 4) | Card in the timeline, `resolved: false` | Nobody gets −1 | Matuludi 85' → −1 (1389) | `Unattributed_goal_and_card…` |
| **Red / second yellow** | `match_events` 20, 21 | As yellow; a red also ends minutes | as yellow | as yellow | as yellow; −2 in total for a second yellow | Inácio Miguel: yellow 81', second yellow 90+1' → −2 total, off at 90, so 90 min but not "full match" (1393) | `AuditGapTests`, `ScoringTests` |
| **Own goal** | `match_events` **15** (not 16; the sync report's §1.3 is wrong) | as goal | as goal | as goal | as goal | (none in GW9) synthetic: −2 | `Penalty_goal_16_scores_plus_three_and_own_goal_15_scores_minus_two` |
| Saves | Team-level only (stat 57) | **Unsupported** | n/a | not shown as player data | Not a scoring category (default decision 3) | — | — |
| Defensive contributions | Team-level only (78, 100); clearances and blocks not stored | **Unsupported** | n/a | — | Not a scoring category | — | — |
| Missed / saved penalty | Event 17 has no attributable keeper | **Partial**, not scored | n/a | timeline only | Not a scoring category | — | — |
| Availability (injuries, suspensions) | Not ingested (`sidelined` not requested) | **Unsupported** | n/a | — | Admin overrides (`app.player_availability_overrides`) are the only source | — | — |

## Confirmations

- **Assist attribution:** a goal's `related_player_id` is the assister (runbook Part 1b; sync coverage sheet; replay pack steps 1–3).
  - It is sometimes absent. That reads as "no assist", not as missing data.
  - Publishable: yes, with that caveat in the rulebook.
- **On-pitch intervals:** minutes come from XI plus sub minutes plus reds, using the inclusive rules. Extra time and stoppage are approximate and capped (rulebook §10).
  - Clean sheets use the whole-match score with the 60-minute threshold. A defender subbed off at 60 before a late concession still loses the clean sheet, because the rule is "team conceded 0 in the match".
  - Sprint 5 adds the pitch-time variant test the runbook asks for and records which rule applies.
