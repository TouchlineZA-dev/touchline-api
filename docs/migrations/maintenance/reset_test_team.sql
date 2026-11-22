-- MAINTENANCE: reset Leruo's test fantasy team for 2026/27. Not a migration; don't number it.
--
-- What:   for the user with auth_user_id 64381143-53e1-423d-bb9e-38232dd486f2, season 2026/27, removes
--         the Gameweek 10 squad, the transfers (and with them the -4 hit), the Gameweek 10 entry
--         (formation, chip, transfer count) and the Bench Boost chip usage, and resets the budget to
--         the column default (100.0).
-- Keeps:  the user, the team row ("Test XI") and its league memberships ("Test League").
-- How:    run as an owner account (postgres) in the Supabase SQL Editor. app_api_user has no DELETE,
--         by design, so the API can't do this itself.
-- Safety: the changes run in one transaction and stop if exactly one team isn't found.
--         To preview without changing anything, replace COMMIT with ROLLBACK below: the "after"
--         column then shows the same values as "before".
--
-- The SQL Editor shows only the last result, so the script saves a "before" snapshot first and
-- ends with one table comparing before and after.

-- 1. Find the team and save a "before" snapshot (no changes yet).
CREATE TEMP TABLE reset_target AS
SELECT t.id AS team_id, u.id AS user_id, s.id AS season_id,
       (SELECT g.id FROM app.gameweeks g WHERE g.season_id = s.id AND g.name = 'Gameweek 10') AS gw10_id
FROM app.users u
JOIN app.fantasy_teams t ON t.user_id = u.id
JOIN football.seasons s ON s.id = t.season_id
JOIN football.competitions c ON c.id = s.competition_id
WHERE u.auth_user_id = '64381143-53e1-423d-bb9e-38232dd486f2'
  AND c.sportmonks_competition_id = 806
  AND s.sportmonks_season_id = 28123;   -- PSL 2026/2027

CREATE TEMP VIEW reset_state AS
SELECT 1 AS ord, 'team' AS item, (SELECT t.name || ' (id ' || t.id || ')' FROM app.fantasy_teams t JOIN reset_target r ON r.team_id = t.id) AS value
UNION ALL SELECT 2, 'budget_remaining', (SELECT t.budget_remaining::text FROM app.fantasy_teams t JOIN reset_target r ON r.team_id = t.id)
UNION ALL SELECT 3, 'GW10 squad rows', (SELECT count(*)::text FROM app.fantasy_squads q JOIN reset_target r ON q.fantasy_team_id = r.team_id AND q.gameweek_id = r.gw10_id)
UNION ALL SELECT 4, 'squad rows, any gameweek', (SELECT count(*)::text FROM app.fantasy_squads q JOIN reset_target r ON q.fantasy_team_id = r.team_id)
UNION ALL SELECT 5, 'GW10 entry (formation / chip / transfers / hit)', (SELECT coalesce(max(e.formation || ' / ' || coalesce(e.chip, 'no chip') || ' / ' || e.transfers_made || ' / -' || e.transfer_cost), 'none')
                                                               FROM app.fantasy_gameweek_entries e JOIN reset_target r ON e.fantasy_team_id = r.team_id AND e.gameweek_id = r.gw10_id)
UNION ALL SELECT 6, 'transfers', (SELECT count(*)::text FROM app.fantasy_transfers x JOIN reset_target r ON x.fantasy_team_id = r.team_id)
UNION ALL SELECT 7, 'chips used this season', (SELECT coalesce(string_agg(c.chip, ', '), 'none') FROM app.fantasy_chip_usage c JOIN reset_target r ON c.fantasy_team_id = r.team_id AND c.season_id = r.season_id)
UNION ALL SELECT 8, 'GW10 points rows', (SELECT count(*)::text FROM app.points_results p JOIN reset_target r ON p.fantasy_team_id = r.team_id AND p.gameweek_id = r.gw10_id)
UNION ALL SELECT 9, 'leagues (kept)', (SELECT coalesce(string_agg(l.name, ', '), 'none') FROM app.league_members m JOIN app.leagues l ON l.id = m.league_id
                                       JOIN reset_target r ON m.fantasy_team_id = r.team_id WHERE m.left_at IS NULL);

CREATE TEMP TABLE reset_before AS SELECT * FROM reset_state;

-- 2. The reset, in one transaction.
BEGIN;

DO $$
BEGIN
    IF (SELECT count(*) FROM reset_target) <> 1 THEN
        RAISE EXCEPTION 'Expected exactly one 2026/27 team for this user, found %. Nothing changed.', (SELECT count(*) FROM reset_target);
    END IF;
    IF (SELECT gw10_id FROM reset_target) IS NULL THEN
        RAISE EXCEPTION 'Gameweek 10 of 2026/27 not found. Nothing changed.';
    END IF;
END $$;

DELETE FROM app.points_results p USING reset_target r
WHERE p.fantasy_team_id = r.team_id AND p.gameweek_id = r.gw10_id;

DELETE FROM app.fantasy_squads q USING reset_target r
WHERE q.fantasy_team_id = r.team_id AND q.gameweek_id = r.gw10_id;

DELETE FROM app.fantasy_transfers x USING reset_target r
WHERE x.fantasy_team_id = r.team_id;

DELETE FROM app.fantasy_gameweek_entries e USING reset_target r
WHERE e.fantasy_team_id = r.team_id AND e.gameweek_id = r.gw10_id;

DELETE FROM app.fantasy_chip_usage c USING reset_target r
WHERE c.fantasy_team_id = r.team_id AND c.season_id = r.season_id;

UPDATE app.fantasy_teams t SET budget_remaining = DEFAULT
FROM reset_target r WHERE t.id = r.team_id;

COMMIT;   -- replace with ROLLBACK to preview without changing anything

-- 3. Before and after, side by side.
SELECT b.item, b.value AS before, a.value AS after
FROM reset_before b
JOIN reset_state a ON a.ord = b.ord
ORDER BY b.ord;
