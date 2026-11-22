# Fantasy logic audit, 2026-11-23

Scope: the fantasy rules in touchline-api (branch `audit/fantasy-2026-11-23`, from `main` at `053f047`), checked against the agreed rules at code level: unit tests, the scoring code run on real 2026/27 data, and live API calls with two test users. No UI.

**Result:** every agreed rule is implemented. Two real bugs were found and fixed (fixtures that don't count could still score; a postponed fixture blocked finalising its gameweek). Rules that were implemented but not proven by a test now have tests (38 new tests, plus 3 for the inclusive minute counting the team decided during the audit). Every live API call returned the expected status. Some behaviour can only be proven with a database or a live match, and is listed under [Not testable here](#not-testable-here).

Status key: ✅ implemented and tested · ⚠️ implemented, verified by code review or live call only · ❌ missing or wrong (none left).

## 1. Traceability

Paths are under `src/Touchline.Api/`; tests are under `tests/Touchline.Api.Tests/`. "Live" means the live API test in [section 4](#4-live-api-test).

### Team and budget

| Rule | Code | Tests | Status |
|---|---|---|---|
| One team per user per season | `Services/FantasyTeamService.CreateTeamAsync`; DB `UNIQUE(user_id, season_id)` | Live: second create → 409 | ⚠️ (DB-level) |
| Budget R100.0m, stored in millions | `Fantasy/FantasyRules.Budget`; `appsettings.json`; `docs/migrations/003` default 100.0 | `Default_budget_and_price_scale_agree` | ✅ |
| Squad total ≤ budget | `SquadRules.ValidateSquad` (maxCost) | `Over_budget_fails`, `Exactly_on_budget_passes` | ✅ |
| budget_remaining after first pick | `SquadRules.BudgetAfterFirstPick` *(new)* | `Budget_after_a_first_pick_is_the_budget_minus_current_prices`; live | ✅ |
| budget_remaining after transfers | `SquadRules.ApplyTransfers` (NewBudgetRemaining) | `Like_for_like_transfer_keeps_two_five_five_three`, `Transfer_over_the_remaining_budget_fails`; live | ✅ |

### Squad

| Rule | Code | Tests | Status |
|---|---|---|---|
| Exactly 2 GK, 5 DEF, 5 MID, 3 ATT | `SquadRules.ValidateSquad` | `Wrong_position_counts_fail`, `Squad_with_one_goalkeeper_fails`, `Squad_with_three_goalkeepers_fails`, `Sixteen_players_fail` | ✅ |
| No duplicates | `SquadRules.ValidateSquad` | `Duplicate_player_fails` | ✅ |
| Max 3 per real club | `SquadRules.ValidateSquad` | `Four_players_from_one_club_fail`, `Three_players_from_one_club_is_fine`, `Transfer_that_breaks_the_club_limit_fails` | ✅ |
| Only players in a 2026/27 lineup are pickable, unless an admin override | `Fantasy/FantasyModels.PlayerAvailability`; `Services/AvailabilityService`; `SquadRules` (requireAvailable, incoming transfers) | `Availability_comes_from_lineups_unless_an_admin_overrides_it`, `Unavailable_player_cannot_be_picked_in_a_first_squad`, `Unavailable_player_cannot_be_transferred_in`, `Unavailable_player_already_in_a_squad_does_not_block_lineup_changes`; live | ✅ |
| XI: exactly 1 GK, formations 4-3-3 / 4-4-2 / 3-5-2 / 3-4-3 / 5-3-2 | `SquadRules.ValidateLineup` | `Every_allowed_formation_gives_a_valid_xi_and_bench`, `Starting_shapes_outside_the_five_formations_fail`, `Starting_xi_with_no_goalkeeper_fails`, `Starting_xi_with_two_goalkeepers_fails`, `Two_defenders_fail_even_if_the_formation_says_otherwise`, `Formation_not_in_the_list_fails`, `Starters_must_match_the_chosen_formation` | ✅ |
| Bench: 1 GK + 3; bench GK swaps only with the starting GK | `SquadRules.ValidateLineup`, `ValidateLineupChange` | `Bench_goalkeeper_swapping_with_an_outfield_player_fails`, `Bench_goalkeeper_swapping_with_the_starting_goalkeeper_is_fine`; live | ✅ |
| Captain and vice-captain, different, both in the XI | `SquadRules.ValidateLineup` | `Captain_on_the_bench_fails`, `Vice_captain_on_the_bench_fails`, `Captain_and_vice_captain_must_differ`, `Missing_captain_fails`, `A_goalkeeper_can_be_captain` | ✅ |
| No captain at the deadline → random starter (same for VC) | `Fantasy/Scoring.CaptaincyRules`; applied in `Services/SquadStore.EnsureAsync` once the gameweek has locked | `Missing_captaincy_is_filled_from_the_starting_xi`, `Existing_captaincy_is_left_alone` | ✅ rule / ⚠️ timing (see Q2) |
| Changes lock 90 min before the first kickoff | `Fantasy/GameweekRules.Deadline`, `IsLocked`; `FantasyContextService.WritableGameweek` | `Deadline_is_ninety_minutes_before_the_first_kickoff`, `Locked_at_and_after_the_deadline`, `Offline_change_for_a_locked_gameweek_is_rejected_with_a_clear_message`; live | ✅ |
| Picks copy forward when a gameweek has none | `Services/SquadStore.LoadAsync`, `EnsureAsync` | Live: GW11 shows GW10's 15 players, not saved | ⚠️ (DB code) |

### Transfers

| Rule | Code | Tests | Status |
|---|---|---|---|
| 1 free per gameweek, no rollover, −4 each extra | `Fantasy/TransferAndChipRules.TransferRules.Calculate` | `One_free_transfer_per_gameweek`, `Each_extra_transfer_costs_four_points`, `Unused_free_transfers_do_not_roll_over`, `Second_request_in_the_same_gameweek_pays_only_its_own_hit`, `Hit_size_is_a_setting`; live | ✅ |
| Unlimited before the GW1 deadline and with Wildcard | `TransferRules.UnlimitedFreeTransfers` *(new, was inline in the service)* | `Unlimited_free_transfers_before_gameweek_one_or_with_wildcard`, `Wildcard_or_before_gameweek_one_is_unlimited_and_free` | ✅ |
| Like for like; squad stays 2/5/5/3 | `SquadRules.ApplyTransfers` | `Transfer_swapping_a_goalkeeper_for_a_defender_fails`, `Like_for_like_transfer_keeps_two_five_five_three`; live | ✅ |
| Selling returns the current price | `SquadRules.ApplyTransfers` (current price, not purchase price) | `Like_for_like_transfer_keeps_two_five_five_three` | ✅ |

### Chips

| Rule | Code | Tests | Status |
|---|---|---|---|
| Each once per season, one per gameweek | `ChipRules.CanActivate`; DB unique constraints | `Same_chip_twice_in_a_season_is_rejected`, `Second_chip_in_the_same_gameweek_is_rejected`, `A_chip_can_be_activated_once`, `Unknown_chip_is_rejected`; live | ✅ |
| Bench Boost: bench counts | `TeamScoring.Score` | `Bench_boost_counts_the_bench` | ✅ |
| Triple Captain ×4; VC gets ×4 if the captain doesn't play | `TeamScoring.Score`; `FantasyRules.TripleCaptainMultiplier` = 4 | `Triple_captain_is_times_four`, `Triple_captain_moves_to_the_vice_captain_when_the_captain_does_not_play` | ✅ |
| Wildcard: unlimited free transfers that gameweek | `TransferRules.UnlimitedFreeTransfers`; `FantasyTeamService.ActivateChipAsync` | `Unlimited_free_transfers_before_gameweek_one_or_with_wildcard` | ✅ |

### Scoring

| Rule | Code | Tests | Status |
|---|---|---|---|
| Only states 5, 7, 8 are final | `Business/MatchStatus.IsFinal` | `Only_states_5_7_8_are_final` | ✅ |
| Fixtures that aren't live or final score nothing | `Business/MatchFactsDecoder.Counts` *(new — bug 1)* | `Fixtures_that_are_not_live_or_final_produce_no_points_even_with_events` (5 states) | ✅ fixed |
| Minutes from lineups (11 = XI, 12 = bench), subs (18: player_id comes on), reds (20, 21) | `Business/MatchFactsDecoder.Decode` *(moved out of the DB service so it can be tested)*; `PlayerScoring.Minutes` | `Lineup_type_11_starts_and_12_is_bench`, `Substitution_player_id_comes_on_and_related_player_goes_off`, `Player_subbed_on_and_then_sent_off_plays_between_the_two_minutes_and_gets_minus_two`, `Substitute_missing_from_the_lineup_still_gets_his_minutes_and_team`, `Red_card_ends_minutes_at_the_card`, `Substitute_plays_from_the_minute_he_came_on`, `Substitution_minutes_add_up_to_the_match_length` *(new)*, `Defender_subbed_off_at_60_has_59_minutes_and_no_clean_sheet` *(new)*; real data (section 3). Counting is inclusive (decision 2026-11-23, see section 2). | ✅ |
| Appearance: 45+ = 1, full match = 2, under 45 = 0 | `PlayerScoring.AppearancePoints` | `Appearance_points_by_minutes` (30/44/45/89), `Full_match_gives_two_appearance_points_in_total`, `Starter_subbed_off_at_89_gets_one_appearance_point`, `Half_time_substitution_at_46_counts_45_minutes_for_both_players` *(new)* | ✅ |
| Goal +3 (14, 16), rescinded ignored | `MatchFactsDecoder`; `PlayerScoring.ScoreFixture` | `Goals_and_penalties_are_three_points_each`, `Goals_penalties_and_assists_are_counted_and_rescinded_goals_ignored` | ✅ |
| Assist +2 from the goal's related player; AssistsEnabled | `MatchFactsDecoder`; `FantasyRules.AssistsEnabled` | `Assists_count_when_enabled_and_not_when_disabled`, `Goals_penalties_and_assists_are_counted_and_rescinded_goals_ignored` | ✅ |
| Clean sheet +2, GK/DEF, 60+ min, team conceded 0 | `PlayerScoring.ScoreFixture`; conceded from the fixture score in `MatchFactsDecoder` | `Clean_sheet_needs_sixty_minutes`, `Clean_sheet_only_for_goalkeepers_and_defenders`, `No_clean_sheet_if_the_team_conceded`, `Goals_conceded_come_from_the_other_teams_score` | ✅ |
| Yellow −1, red −2, second yellow −2 total, own goal −2 | `PlayerScoring.ScoreFixture`; `MatchFactsDecoder` | `Yellow_card_is_minus_one`, `Red_card_is_minus_two`, `Second_yellow_counts_as_a_red_only`, `Second_yellow_after_a_yellow_is_minus_two_in_total_and_ends_minutes`, `Own_goal_is_minus_two`, `Own_goal_is_counted_for_the_player_who_scored_it` | ✅ |
| Captain ×3, VC ×2, VC ×3 if the captain played 0 minutes | `TeamScoring.Score` | `Captain_scores_triple_and_vice_captain_double`, `Vice_captain_gets_triple_when_the_captain_does_not_play`, `Captain_and_vice_captain_both_not_playing_score_zero_and_nobody_else_is_promoted` *(new)* | ✅ |
| No auto-subs; bench only with Bench Boost | `TeamScoring.Score` | `No_auto_subs_a_starter_who_did_not_play_scores_zero_and_the_bench_does_not_replace_him`, `Bench_never_scores_without_bench_boost` | ✅ |
| Double gameweeks sum | `PlayerScoring.ScoreGameweek` | `Double_gameweek_points_add_up`, `Double_gameweek_sums_both_fixtures` *(new, through the decoder)* | ✅ |
| Transfer hits subtracted | `TeamScoring.Score` | `Transfer_hits_are_subtracted` | ✅ |
| Re-score updates, never duplicates | `PointsService.FinaliseAsync` (update existing rows); DB unique index `uq_points_results_team_player_gw` | — (needs a database) | ⚠️ |
| Breakdown JSON stored | `Fantasy/Scoring.PointsBreakdownJson` *(new, was private in PointsService)* | `Points_breakdown_json_round_trips_and_has_the_agreed_keys` | ✅ |

### Live points

| Rule | Code | Tests | Status |
|---|---|---|---|
| Provisional during matches, flagged | `PlayerScoring` (Provisional), `TeamScoring`, `GameweekRules.LiveMinute` | `Live_fixture_gives_provisional_points_and_finished_gives_final`, `Live_fixture_produces_provisional_facts_up_to_the_current_minute`, `Live_clean_sheet_is_provisional`, `Team_score_is_provisional_while_any_player_is_provisional`, `Live_minute_from_the_clock`, `Live_minute_never_lags_the_latest_event` | ✅ |
| Not saved; cached 60 s | `PointsService.PlayerPointsAsync` (IMemoryCache, `LivePointsCacheSeconds`) | — | ⚠️ |
| Final only when the gameweek is complete | `GameweekRules.CanFinalise` *(fixed — bug 2)*; `PointsService.FinaliseAsync` | `Finalise_only_when_marked_complete_or_every_fixture_is_final`, `Gameweek_with_a_postponed_fixture_can_still_be_finalised` | ✅ fixed |

### Leagues

| Rule | Code | Tests | Status |
|---|---|---|---|
| Private, join by code only (no list endpoint) | `Controllers/FantasyControllers` (no public list), `LeagueService.JoinAsync` | Live | ✅ |
| Code "TL-" + 6 digits, 2 of # ! @ - between them, unique | `LeagueRules.GenerateJoinCode`, `IsValidJoinCode`, `GenerateUniqueJoinCodeAsync`; DB unique | `Join_codes_have_the_right_format` (500 codes), `Join_code_validation`, `Taken_join_codes_are_retried`, `Gives_up_after_the_maximum_attempts` | ✅ |
| Owner = creator, auto-joins, can't leave | `LeagueService.CreateAsync`, `LeaveAsync`; `LeagueRules.CanLeave` | `Owner_cannot_leave`; live | ✅ |
| Max 20 teams, under a row lock | `LeagueRules.CanJoin`; `LeagueService.JoinAsync` (`SELECT … FOR UPDATE`) | `League_cap_is_twenty_including_the_owner` | ✅ rule / ⚠️ concurrency |
| A team can be in many leagues | Schema: unique only per (league, team) | Live | ✅ |
| Leaving sets left_at | `LeagueService.LeaveAsync` | Live | ⚠️ (DB code) |
| Overall = every team | `LeagueService.StandingsAsync("overall")` | Live | ⚠️ (DB code) |
| Season total + single gameweek; ties: overall points, then username | `LeagueRules.Rank`; `PointsService.TeamTotalsAsync` | `Standings_tie_break_is_overall_points_then_username`; live | ✅ |

### Offline

| Rule | Code | Tests | Status |
|---|---|---|---|
| Snapshot endpoint | `FantasyTeamService.SnapshotAsync` | Live | ⚠️ (DB code) |
| ETag / Cache-Control / 304 | `Controllers/FantasyFilters.ETagAttribute` | Live (304) | ⚠️ |
| Stale If-Match → 409 | `Fantasy/GameweekRules.SquadVersion`; `FantasyContextService.CheckIfMatchAsync` | `Stale_version_is_detected`, `Stale_if_match_returns_conflict_with_the_current_state`, `Unreadable_if_match_is_a_bad_request`; live | ✅ |
| Offline change after the deadline rejected | `FantasyContextService.WritableGameweek` | `Offline_change_for_a_locked_gameweek_is_rejected_with_a_clear_message`; live | ✅ |

### Prices

| Rule | Code | Tests | Status |
|---|---|---|---|
| Recalculated per transfer window, once unless forced | `PlayerValuation.CanRecalculate`; `PricingService.RecalculateAsync` | `Recalculating_the_same_window_twice_is_refused_without_force`, `Pre_season_run_is_refused_after_the_gameweek_one_deadline` | ✅ |
| R per 90 from our scoring rules | `PlayerValuation.PointsPer90`; `PricingService.SeasonStatsAsync` (uses `PlayerScoring`) | `Points_per_90` | ✅ |
| Shrinkage K = 900 | `PlayerValuation.Shrink`, `PositionAverage` | `Small_sample_is_pulled_close_to_the_position_average`, `Big_sample_barely_moves`, `Position_average_is_weighted_by_minutes` | ✅ |
| E with 0.6/0.3/0.1, rescaled; seasons without data skipped | `PlayerValuation.ExpectedPoints`, `RescaledWeights`, `CandidateSeasons`, `SeasonsWithData` | `Weights_rescale_with_two_seasons`, `Weights_rescale_with_one_season`, `Expected_points_with_two_seasons_uses_the_rescaled_weights`, `Seasons_without_data_are_skipped_and_older_ones_used_instead`, `Mid_season_run_uses_the_current_season_as_R1` | ✅ |
| Percentile within position | `PlayerValuation.Percentiles` | `Percentiles_lowest_zero_highest_one_ties_share`, `Lone_player_gets_percentile_half` | ✅ |
| V = 0.5 × round(… ÷ 0.5), halves up | `PlayerValuation.Price`, `RoundToStep` | `Prices_round_to_half_a_million_with_halves_up`, `Rounding_exactly_on_quarters_goes_up` *(new: 4.25, 4.75, 6.25, 6.75, 12.25)*, `Defender_price_landing_on_a_quarter_rounds_up` *(new)* | ✅ |
| Ranges × PriceScale | `PricingRules.RangeFor` | `Every_formula_price_stays_inside_its_position_range`, `Lowest_and_highest_in_a_position_get_the_min_and_max_price`, `Price_scale_multiplies_the_ranges` | ✅ |
| No history → position minimum; overrides win | `PlayerValuation.PriceAll` | `New_player_with_no_history_gets_the_position_minimum`, `Price_override_wins_over_the_formula`, `Expected_minutes_override_replaces_M` | ✅ |
| One transaction; price_changes insert only | `PricingService.RecalculateAsync` | — (needs a database; code review) | ⚠️ |
| Worked example: E 146.7 → R11.5m | `PlayerValuation` | `Worked_example_gives_E_146_7_and_price_11_5` | ✅ |

### Admin and security

| Rule | Code | Tests | Status |
|---|---|---|---|
| Admin endpoints need is_admin | `Services/AdminService.RequireAdminAsync` | Live: non-admin → 403 | ⚠️ (DB-level) |
| User from the token, never the body | `Users/CurrentUser` (`sub`); `FantasyControllerBase.MeAsync`; request bodies have no user field | `Only_me_fantasy_and_admin_require_authentication` and the token tests in `Auth/TokenValidationTests` | ✅ |
| No writes to football / sync | `Data/TouchlineDbContext.GuardReadOnlySchemas`; DB grants (SELECT only) | `Writes_to_football_and_sync_are_refused_before_reaching_the_database` *(new)* | ✅ |
| No DELETE on app tables | Code: no DbSet `Remove`/`RemoveRange`, `ExecuteDelete` or `DELETE` SQL in `src/` (the only `.Remove` calls are on in-memory lists and the cache); DB: app_api_user has no DELETE on any app table (checked 2026-11-22) | Code search + privilege query | ⚠️ |

## 2. Bugs found and fixed

1. **Fixtures that don't count could still score.** `MatchFactsService` built facts for every fixture in a gameweek. An abandoned match (state 15), or one "awaiting updates" (19), can have goals and cards recorded, and those were scored, although only live or final fixtures count. Fixed: `MatchFactsDecoder.Counts` lets only Live and Finished fixtures produce points, and the service doesn't load rows for the rest. Test: `Fixtures_that_are_not_live_or_final_produce_no_points_even_with_events`.
2. **A gameweek with a postponed fixture could never be finalised.** `CanFinalise` required every fixture to be final, so one postponed match kept its gameweek open for good (unless the sync marked it complete, which it doesn't do yet). Fixed: a gameweek is finalisable when at least one fixture is final and the rest are final or won't be played (postponed, cancelled, abandoned). A rescheduled match counts in whichever gameweek its new date falls in. "Awaiting updates" and live fixtures still block. Test: `Gameweek_with_a_postponed_fixture_can_still_be_finalised`. Agreed by the team on 2026-11-23.

**Team decisions taken during the audit (2026-11-23):**

- **Inclusive minute counting.** A player coming on in minute m plays (length − m + 1) minutes; a player going off in minute m (sub or red card) plays (m − 1). Half-time subs (recorded at 46', 79 so far this season) now get 45 minutes and their appearance point, where before they got 44 and nothing. Side effect: a defender or keeper subbed off at 60' now has 59 minutes, one short of a clean sheet. Code: `PlayerScoring.Minutes`. Tests updated and added.
- **Finalising with a postponed fixture:** bug 2's fix is agreed.

Refactors made to get code under test, with no behaviour change: lineup/event decoding moved from `MatchFactsService` into the pure `Business/MatchFactsDecoder`; `TransferRules.UnlimitedFreeTransfers`, `SquadRules.BudgetAfterFirstPick` and `PointsBreakdownJson` extracted from services.

## 3. Real-data scoring check

The API's own `MatchFactsService` and `PlayerScoring` run, read-only, on three finished 2026/27 fixtures (31 Oct 2026), with the inclusive minute counting agreed on 2026-11-23. For each player: the raw lineup and events, then the result.

**Fixture 1394: Stellenbosch 0–2 Chippa United (state 5, length 90)**

| Player | Picked as | Raw lineup | Raw events | Result | Total |
|---|---|---|---|---|---|
| Harold Majadibodu (DEF, Chippa United) | scorer | type_id 11 (starting XI) | 27' type 14 goal — scorer; 76' type 18 sub — OFF (related) | 75 min; app 1; goals 1 (+3); assists 0 (0); CS 1 (+2, conceded 0); yellow 0; red 0 | **6** |
| Imraan Jones (DEF, Chippa United) | scorer | type_id 11 (starting XI) | 83' type 14 goal — scorer | 90 min; app 2; goals 1 (+3); assists 0 (0); CS 1 (+2, conceded 0); yellow 0; red 0 | **7** |
| Stanley Nwabali (GK, Chippa United) | GK, clean sheet | type_id 11 (starting XI) | — | 90 min; app 2; goals 0 (0); assists 0 (0); CS 1 (+2, conceded 0); yellow 0; red 0 | **4** |
| Quwan Christiano Plaatjies (ATT, Stellenbosch) | came on | type_id 12 (bench) | 58' type 18 sub — ON (player_id) | 33 min; app 0; goals 0 (0); assists 0 (0); CS 0 (0, conceded 2); yellow 0; red 0 | **0** |
| Lehlogonolo Matlou (MID, Stellenbosch) | card | type_id 11 (starting XI) | 32' type 19 yellow — player; 81' type 18 sub — OFF (related) | 80 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 2); yellow -1; red 0 | **0** |

**Fixture 1391: AmaZulu 1–0 Golden Arrows (state 5, length 90)**

| Player | Picked as | Raw lineup | Raw events | Result | Total |
|---|---|---|---|---|---|
| Thandolwenkosi Methuseli Ngwenya (ATT, AmaZulu) | scorer | type_id 11 (starting XI) | 65' type 14 goal — scorer; 86' type 18 sub — OFF (related) | 85 min; app 1; goals 1 (+3); assists 0 (0); CS 0 (0, conceded 0); yellow 0; red 0 | **4** |
| Olwethu Mzimela (GK, AmaZulu) | GK, clean sheet | type_id 11 (starting XI) | — | 90 min; app 2; goals 0 (0); assists 0 (0); CS 1 (+2, conceded 0); yellow 0; red 0 | **4** |
| Siphesile Msomi (MID, Golden Arrows) | came on | type_id 12 (bench) | 46' type 18 sub — ON (player_id) | 45 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 1); yellow 0; red 0 | **1** |
| Nhlanhla Zwane (MID, Golden Arrows) | card | type_id 11 (starting XI) | 34' type 19 yellow — player; 59' type 18 sub — OFF (related) | 58 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 1); yellow -1; red 0 | **0** |
| Siyanda Hlangabeza (MID, AmaZulu) | card | type_id 11 (starting XI) | 39' type 19 yellow — player; 68' type 18 sub — OFF (related) | 67 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 0); yellow -1; red 0 | **0** |

**Fixture 1393: Orlando Pirates 2–1 Kaizer Chiefs (state 5, length 90)**

| Player | Picked as | Raw lineup | Raw events | Result | Total |
|---|---|---|---|---|---|
| Yanela Mbuthuma (ATT, Orlando Pirates) | scorer | type_id 11 (starting XI) | 24' type 14 goal — scorer; 75' type 18 sub — OFF (related) | 74 min; app 1; goals 1 (+3); assists 0 (0); CS 0 (0, conceded 1); yellow 0; red 0 | **4** |
| Pule Mmodi (ATT, Kaizer Chiefs) | scorer | type_id 11 (starting XI) | 30' type 14 goal — scorer; 82' type 18 sub — OFF (related) | 81 min; app 1; goals 1 (+3); assists 0 (0); CS 0 (0, conceded 2); yellow 0; red 0 | **4** |
| Ghampani Lungu (ATT, Orlando Pirates) | came on | type_id 12 (bench) | 46' type 18 sub — ON (player_id) | 45 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 1); yellow 0; red 0 | **1** |
| Reeve Frosler (DEF, Kaizer Chiefs) | card | type_id 11 (starting XI) | 8' type 19 yellow — player; 69' type 18 sub — OFF (related) | 68 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 2); yellow -1; red 0 | **0** |
| Simphiwe Selepe (MID, Orlando Pirates) | card | type_id 11 (starting XI) | 38' type 19 yellow — player; 59' type 18 sub — OFF (related) | 58 min; app 1; goals 0 (0); assists 0 (0); CS 0 (0, conceded 1); yellow -1; red 0 | **0** |

Hand check, for example:

- Majadibodu started (type 11) and went off in minute 76 → 75 minutes → appearance 1; goal at 27' → +3; Chippa conceded 0 and he played 60+ → clean sheet +2; total **6**.
- Msomi came on in minute 46 (half-time) → 90 − 46 + 1 = **45 minutes** → appearance 1. Before the inclusive-counting decision he had 44 minutes and 0.
- Plaatjies came on in minute 58 → 90 − 58 + 1 = 33 minutes → no appearance point.
- Ngwenya is an attacker, so no clean sheet although AmaZulu conceded 0.

Every total in the tables matches the rules worked by hand. None of these 15 players had an assist recorded (assists are proven by unit tests). No rescinded events exist in 2024/25 or 2026/27 so far.

## 4. Live API test

Run on 2026-11-23 against the live Supabase database, through the running API, with two test users. Only Gameweek 10 was changed (deadline Tue 24 Nov 2026, 18:00 SAST, still open). Tokens were read from `.env` and never printed.

- **User 1**: an admin account that already had a 2026/27 team (Test XI: GW10 squad, Bench Boost used, 2 transfers and a −4 hit).
- **User 2**: a new non-admin account with no team.

The script stopped twice on its own bugs (a lookup for a bench defender when all five were starting, and an ETag variable not carried into the resumed run). It was resumed from the failing step each time, without repeating any call that changes data. The stale ETag in the resumed run was a version-1 tag, which is just as stale.

**Every call returned the expected status.**

### User 1 (admin, existing team)

| Call | Expected | Got | Result |
|---|---|---|---|
| GET /me | 200 | 200 | profile |
| POST /fantasy/team | 409 | 409 | "You already have a team for this season. Each user has one team per season." |
| GET /fantasy/team/squad?gameweek=10 | 200 | 200 | 3-5-2, Bench Boost, 2 transfers, hit −4, 0 free, R36.0m, version 5 |
| PUT /fantasy/team/lineup (If-Match current) | 200 | 200 | 3-5-2 → 4-4-2, version 6 |
| PUT /fantasy/team/lineup (swap → 4-5-1) | 400 | 400 | "'4-5-1' isn't an allowed formation…" / "The starting XI needs 2 or 3 attackers; you have 1." |
| PUT /fantasy/team/lineup (bench GK ↔ outfield) | 400 | 400 | "The bench goalkeeper can only swap with the starting goalkeeper." (+ 3 related errors) |
| POST /fantasy/team/transfers (like for like) | 200 | 200 | free transfer already used → cost 4, gameweek hit 8, budget unchanged R36.0m |
| POST /fantasy/team/transfers (GK out, DEF in) | 400 | 400 | "You're selling 1 goalkeeper but buying 0…" |
| POST /fantasy/team/chips triple_captain | 400 | 400 | "Bench Boost is already active this gameweek. Only one chip per gameweek." |
| POST /fantasy/team/chips bench_boost | 400 | 400 | "You've already used Bench Boost this season." |
| PUT /fantasy/team/lineup (stale If-Match v5) | 409 | 409 | "Team changed", current team (version 7) included |
| GET squad with matching If-None-Match | 304 | 304 | not modified |
| PUT /fantasy/team/lineup for gameweek 9 | 409 | 409 | "Gameweek 9 locked at 2026-10-30 16:00 UTC, so this change wasn't saved. Changes now apply to Gameweek 10." |
| GET /admin/transfer-windows | 200 | 200 | admin allowed |
| POST /fantasy/leagues | 201 | 201 | "Audit League", join code `TL-77-466!8`, owner, 1 member |

### User 2 (non-admin, from scratch)

| Call | Expected | Got | Result |
|---|---|---|---|
| GET /me | 200 | 200 | profile created on first call |
| POST /fantasy/team | 201 | 201 | "Audit FC", budget R100.0m, next gameweek 10 |
| POST /fantasy/team (again) | 409 | 409 | one team per season |
| PUT /squad, 3 goalkeepers | 400 | 400 | "A squad needs exactly 2 goalkeepers; you have 3." (+2) |
| PUT /squad, 4 from one club | 400 | 400 | "Max 3 players from Kruger United; you have 4." |
| PUT /squad, 2 defenders starting | 400 | 400 | "The starting XI needs 3 to 5 defenders; you have 2." (+1) |
| PUT /squad, captain on the bench | 400 | 400 | "The captain must be in the starting XI." |
| PUT /squad, unavailable player | 400 | 400 | "A. El Maswab isn't available to pick: he hasn't been in a PSL matchday squad this season." |
| PUT /squad, valid | 200 | 200 | 4-4-2, cost R64.0m, budget left **R36.0m** (= 100.0 − 64.0) |
| PUT /squad, again | 409 | 409 | "You've already picked your squad…" |
| GET /squad?gameweek=10 | 200 | 200 | version 1 |
| GET /squad?gameweek=11 | 200 | 200 | **copy forward**: same 15 players, same formation, not saved |
| PUT /lineup | 200 | 200 | 4-4-2 → 5-3-2 |
| PUT /lineup (→ 5-4-1) | 400 | 400 | "'5-4-1' isn't an allowed formation…" |
| POST /transfers, 1st | 200 | 200 | **free**: cost 0, 0 free left |
| POST /transfers, 2nd | 200 | 200 | **−4**: cost 4, gameweek hit 4 |
| POST /transfers, GK out, DEF in | 400 | 400 | "You're selling 1 goalkeeper but buying 0…" |
| POST /transfers, unavailable player in | 400 | 400 | "A. El Maswab isn't available to pick…" |
| POST /chips bench_boost | 200 | 200 | chip active |
| POST /chips triple_captain | 400 | 400 | "Bench Boost is already active this gameweek. Only one chip per gameweek." |
| PUT /lineup (stale If-Match v1) | 409 | 409 | "Team changed", current team included |
| GET squad with matching If-None-Match | 304 | 304 | not modified |

### Leagues, standings, snapshots, admin

| Who | Call | Expected | Got | Result |
|---|---|---|---|---|
| 2 | POST /leagues/join `TL-000000` | 400 | 400 | "That isn't a league code. Codes look like TL-482#91!3." |
| 2 | POST /leagues/join `TL-12#34@56` | 404 | 404 | "No league has that code." |
| 2 | POST /leagues/join with user 1's code | 200 | 200 | joined, 2 members |
| 2 | POST /leagues/join again | 409 | 409 | "Your team is already in this league." |
| 2 | GET /leagues | 200 | 200 | Overall and Audit League; **join code hidden** from the non-owner |
| 1 | GET /leagues/3/standings | 200 | 200 | 1. Audit FC 0, 2. Test XI 0 (tie → username) |
| 2 | POST /leagues/3/leave | 204 | 204 | left (left_at set) |
| 2 | GET /leagues/3/standings | 403 | 403 | "Only members can see this league's standings." |
| 1 | POST /leagues/3/leave (owner) | 400 | 400 | "You own this league, so you can't leave it." |
| 1 | GET /leagues/3/standings | 200 | 200 | only Test XI left |
| 1 | GET /leagues/overall/standings | 200 | 200 | 2 teams (every team in the season) |
| 1 | GET /leagues/overall/standings?gameweek=10 | 200 | 200 | single-gameweek view (not started yet, not provisional) |
| 1 | GET /fantasy/team/snapshot | 200 | 200 | Test XI: v7, 4-4-2, Bench Boost, hit −8, 0 free, R36.0m, 3 leagues |
| 2 | GET /fantasy/team/snapshot | 200 | 200 | Audit FC: v5, 5-3-2, Bench Boost, hit −4, 0 free, R36.0m, Overall only |
| 2 | GET /admin/transfer-windows | 403 | 403 | "Only admins can do this." |
| 2 | POST /admin/prices/preview | 403 | 403 | "Only admins can do this." |
| 2 | PUT /admin/players/1/available | 403 | 403 | "Only admins can do this." |
| – | GET /fantasy/team (no token) | 401 | 401 | Unauthorized |

**Data left behind by the test:** user 1's Test XI now has a third transfer (gameweek hit −8) and a 4-4-2 lineup for GW10; "Audit League" (owner user 1) exists; user 2 has "Audit FC" with a GW10 squad, 2 transfers (−4) and Bench Boost used. `docs/migrations/maintenance/reset_test_team.sql` resets user 1's team; user 2's team is a throwaway.

## Not testable here

- **Live points during a real match.** The sync last wrote events and lineups on 1 Nov 2026 and writes in daily batches (around 23:00 UTC), so there is no in-play data to watch. Provisional scoring is covered by unit tests only.
- **2025/26 prices.** 2025/26 has 240 final fixtures but no lineups or events, so it can't feed the price formula; only 2024/25 does.
- **Finalising a gameweek end to end, and re-scoring it.** No test team has a squad in a finished gameweek, and finalising writes to the shared database. The update-not-duplicate logic and the unique index were checked by reading the code.
- **20th and 21st league joins at the same moment.** Needs 21 teams or a disposable database. The row lock (`SELECT … FOR UPDATE` on the league row inside the join transaction) was checked by reading the code.
- **The 14 Postgres-backed `/me` tests** need Docker, which isn't installed on this machine. They are skipped locally; CI runs them (`TOUCHLINE_REQUIRE_DB_TESTS=true`).
- **The 60-second live-points cache.** Needs the database-backed facts service; checked by reading the code.

## Questions for the team

None of these were changed; they need a decision.

1. **First-half states.** Only state 22 (2nd half) counts as live. States 2 (1st half) and 3 (half-time) aren't in our code list because they've never appeared in the data. If they do appear, live points wouldn't count until the second half. Add them?
2. **When the random captain is picked.** The random pick happens when the team is next written to after the deadline, or when the gameweek is finalised, not at the deadline itself. Until then provisional points show no captain bonus. In practice a team can't be left without a captain (saves require one, and copies and transfers keep it), so this only affects old data.
3. **Assists rely on the goal's related player.** 101 of 158 goals this season have one. Keep `AssistsEnabled` on?

## Recommendations

1. Run the 14 Docker-only tests in CI on this PR, and add a Testcontainers test for simultaneous league joins and for finalising twice.
2. Ask the sync owner to restart event and lineup updates (stopped on 1 Nov) and, if possible, backfill 2025/26 lineups and events.
3. Decide Q1–Q3 above.
4. Apply `docs/migrations/006` (player availability overrides) so `PUT /admin/players/{id}/available` works.
