# Fantasy logic

Squad validation, transfers, chips, scoring and leaderboard ranking go here, as
plain C# with no database code, so the rules can be unit-tested on their own.

Not started yet. Before writing rules, settle these open questions from the
Touchline vault (`Project/Open Questions.md`):

- #1 Which scoring system (whiteboard, old app, or backend guide)
- #2 Budget scale (the `app` schema default is 300.0)
- #12 Transfers and point hits
- #13 Chip reuse per season
- #14 Clean-sheet points for players who did not play
- #15 Auto-subs
- #19 Which `app` schema is canonical (sync Flyway migrations vs the frontend's
  `20261122090000_fantasy_api.sql`)

Data rules that always apply (BACKEND_INTEGRATION.md §6–§9):

- Score a fixture only when `state_id` is 5, 7 or 8 (`MatchStatusExtensions.IsFinal`).
- Bridge app picks to events through `football.players`
  (`fantasy_squads.player_id = players.id`, then
  `players.sportmonks_player_id = match_events.sportmonks_player_id`).
- No per-player minutes, assists or ratings on the current Sportmonks plan.
