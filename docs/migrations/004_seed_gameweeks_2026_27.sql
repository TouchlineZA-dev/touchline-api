-- 004: seed the 2026/27 PSL gameweeks.
--
-- What:       28 rows in app.gameweeks, "Gameweek 1" to "Gameweek 28". One gameweek per Monday–Sunday
--             week (SAST) that has 2026/27 PSL league fixtures; deadline_at is 90 minutes before that
--             week's first kickoff (Fantasy:DeadlineMinutesBeforeFirstKickoff).
-- Source:     generated from football.fixtures on 2026-11-22 (240 fixtures: 69 finished, 164 scheduled,
--             7 postponed). Postponed (10), cancelled (12) and abandoned (15) fixtures don't create a
--             week or move a deadline, which is why the week of 2 Nov 2026 (all 7 of its fixtures
--             postponed) has no gameweek.
-- Depends on: 003 (app.gameweeks.deadline_at) and the existing season_id column on app.gameweeks.
-- How:        applied by hand in the Supabase SQL Editor by an owner account (postgres), after 003.
-- Re-running: safe. A week is skipped if the season already has a gameweek starting that day.
--
-- Review before applying:
--   - Gameweek 3 has a single fixture (Mamelodi Sundowns v Marumo Gallants, Wed 19 Aug), and
--     Gameweek 1 has 7 instead of 8: that match looks like Gameweek 1's missing fixture played late.
--   - Gameweeks 2 and 12 have 16 fixtures (two rounds in one week).
--   - The 7 fixtures postponed from Wed 4 Nov have no gameweek yet. When they're rescheduled they
--     count in whichever gameweek's dates they fall in; if they land in a week with no gameweek,
--     create one with POST /api/v1/admin/gameweeks.
--   - Deadlines are stored. If kickoff times change later, update deadline_at for future gameweeks.

WITH season AS (
    SELECT s.id
    FROM football.seasons s
    JOIN football.competitions c ON c.id = s.competition_id
    WHERE c.sportmonks_competition_id = 806 AND s.sportmonks_season_id = 28123   -- PSL 2026/2027
),
weeks (number, start_date, end_date, deadline_at) AS (
    VALUES
    ( 1, DATE '2026-07-27', DATE '2026-08-02', TIMESTAMPTZ '2026-08-01T11:30:00Z'),  -- 7 fixtures
    ( 2, DATE '2026-08-10', DATE '2026-08-16', TIMESTAMPTZ '2026-08-11T16:00:00Z'),  -- 16 fixtures
    ( 3, DATE '2026-08-17', DATE '2026-08-23', TIMESTAMPTZ '2026-08-19T16:00:00Z'),  -- 1 fixture
    ( 4, DATE '2026-08-24', DATE '2026-08-30', TIMESTAMPTZ '2026-08-25T16:00:00Z'),  -- 8 fixtures
    ( 5, DATE '2026-08-31', DATE '2026-09-06', TIMESTAMPTZ '2026-09-01T16:00:00Z'),  -- 9 fixtures
    ( 6, DATE '2026-09-07', DATE '2026-09-13', TIMESTAMPTZ '2026-09-09T16:00:00Z'),  -- 8 fixtures
    ( 7, DATE '2026-09-14', DATE '2026-09-20', TIMESTAMPTZ '2026-09-17T16:00:00Z'),  -- 8 fixtures
    ( 8, DATE '2026-10-12', DATE '2026-10-18', TIMESTAMPTZ '2026-10-13T16:00:00Z'),  -- 8 fixtures
    ( 9, DATE '2026-10-26', DATE '2026-11-01', TIMESTAMPTZ '2026-10-30T16:00:00Z'),  -- 8 fixtures
    (10, DATE '2026-11-23', DATE '2026-11-29', TIMESTAMPTZ '2026-11-24T16:00:00Z'),  -- 8 fixtures
    (11, DATE '2026-11-30', DATE '2026-12-06', TIMESTAMPTZ '2026-12-04T16:00:00Z'),  -- 8 fixtures
    (12, DATE '2026-12-14', DATE '2026-12-20', TIMESTAMPTZ '2026-12-15T16:00:00Z'),  -- 16 fixtures
    (13, DATE '2027-01-04', DATE '2027-01-10', TIMESTAMPTZ '2027-01-08T16:00:00Z'),  -- 8 fixtures
    (14, DATE '2027-01-11', DATE '2027-01-17', TIMESTAMPTZ '2027-01-15T16:00:00Z'),  -- 8 fixtures
    (15, DATE '2027-01-18', DATE '2027-01-24', TIMESTAMPTZ '2027-01-22T16:00:00Z'),  -- 8 fixtures
    (16, DATE '2027-02-01', DATE '2027-02-07', TIMESTAMPTZ '2027-02-05T16:00:00Z'),  -- 8 fixtures
    (17, DATE '2027-02-08', DATE '2027-02-14', TIMESTAMPTZ '2027-02-12T16:00:00Z'),  -- 8 fixtures
    (18, DATE '2027-02-22', DATE '2027-02-28', TIMESTAMPTZ '2027-02-26T16:00:00Z'),  -- 8 fixtures
    (19, DATE '2027-03-01', DATE '2027-03-07', TIMESTAMPTZ '2027-03-05T16:00:00Z'),  -- 8 fixtures
    (20, DATE '2027-03-08', DATE '2027-03-14', TIMESTAMPTZ '2027-03-12T16:00:00Z'),  -- 8 fixtures
    (21, DATE '2027-03-15', DATE '2027-03-21', TIMESTAMPTZ '2027-03-16T16:00:00Z'),  -- 8 fixtures
    (22, DATE '2027-04-05', DATE '2027-04-11', TIMESTAMPTZ '2027-04-09T16:00:00Z'),  -- 8 fixtures
    (23, DATE '2027-04-12', DATE '2027-04-18', TIMESTAMPTZ '2027-04-16T16:00:00Z'),  -- 8 fixtures
    (24, DATE '2027-04-19', DATE '2027-04-25', TIMESTAMPTZ '2027-04-23T16:00:00Z'),  -- 8 fixtures
    (25, DATE '2027-04-26', DATE '2027-05-02', TIMESTAMPTZ '2027-04-27T16:00:00Z'),  -- 8 fixtures
    (26, DATE '2027-05-03', DATE '2027-05-09', TIMESTAMPTZ '2027-05-08T11:30:00Z'),  -- 8 fixtures
    (27, DATE '2027-05-10', DATE '2027-05-16', TIMESTAMPTZ '2027-05-15T11:30:00Z'),  -- 8 fixtures
    (28, DATE '2027-05-17', DATE '2027-05-23', TIMESTAMPTZ '2027-05-22T11:30:00Z')   -- 8 fixtures
)
INSERT INTO app.gameweeks (name, start_date, end_date, is_active, season_id, deadline_at)
SELECT 'Gameweek ' || w.number, w.start_date, w.end_date, false, season.id, w.deadline_at
FROM weeks w
CROSS JOIN season
WHERE NOT EXISTS (
    SELECT 1 FROM app.gameweeks g WHERE g.season_id = season.id AND g.start_date = w.start_date
)
ORDER BY w.number;
