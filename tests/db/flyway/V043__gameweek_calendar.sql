-- V043: mirrored from touchline-api docs/migrations/007_gameweek_calendar.sql (keep the two identical).
-- 007 (touchline-sync V043): a persisted fantasy calendar.
--
-- Why: the API derived gameweek numbers from row order and fixture membership from SAST date
-- windows at read time. An inserted early week renumbered every later week, and a rescheduled
-- fixture silently changed gameweek. Handbook Gate 2: persist an immutable gameweek id, a public
-- number and the season, plus an explicit fixture→gameweek assignment with revision history.
--
-- What:
--   1. app.gameweeks.number: the public gameweek number, unique per season. Backfilled from the
--      existing order (the 28 seeded 2026/27 gameweeks become 1–28).
--   2. app.fixture_gameweek_assignments: insert-only history. The current assignment of a fixture is
--      its newest row (highest id). gameweek_id NULL means "unassigned" (e.g. postponed with no new
--      date). Every row records when, why and who (assigned_by_user_id NULL = migration/system).
--      No foreign key to football.fixtures on purpose: the sync worker owns football rows and must
--      never be blocked by an app-schema constraint.
--   3. Backfill: every non-placeholder fixture of a season that has gameweeks, assigned to the gameweek
--      whose SAST dates contain its kickoff (the rule the API used until now). Postponed (10),
--      cancelled (12) and abandoned (15) fixtures are not backfilled: they stay unassigned until an
--      admin assigns them (team default decision 1, 2026-11-23).
--   4. Grants: app_api_user SELECT, INSERT on the history (no UPDATE needed, never DELETE); RLS on with
--      the api_full_access policy (same pattern as V037).
--
-- Safe to rerun: IF NOT EXISTS everywhere, the number backfill only fills NULLs, and fixtures that
-- already have an assignment row are skipped.

-- 1. Public gameweek number --------------------------------------------------------------------
ALTER TABLE app.gameweeks ADD COLUMN IF NOT EXISTS number SMALLINT;

UPDATE app.gameweeks g
SET number = ordered.n
FROM (
    SELECT id, row_number() OVER (PARTITION BY season_id ORDER BY start_date, id) AS n
    FROM app.gameweeks
    WHERE season_id IS NOT NULL
) ordered
WHERE g.id = ordered.id AND g.number IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_gameweeks_season_number
    ON app.gameweeks (season_id, number) WHERE number IS NOT NULL;

COMMENT ON COLUMN app.gameweeks.number IS
    'Public gameweek number within the season. Persisted, never derived from row order. Set when the gameweek is created.';

-- 2. Fixture → gameweek assignment history ------------------------------------------------------
CREATE TABLE IF NOT EXISTS app.fixture_gameweek_assignments (
    id                   SERIAL PRIMARY KEY,
    fixture_id           INTEGER     NOT NULL,                       -- football.fixtures.id (no FK, see above)
    gameweek_id          INTEGER     REFERENCES app.gameweeks (id),  -- NULL = unassigned
    reason               TEXT        NOT NULL,
    assigned_by_user_id  INTEGER     REFERENCES app.users (id),      -- NULL = migration or system
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_fixture_gameweek_assignments_fixture
    ON app.fixture_gameweek_assignments (fixture_id, id DESC);
CREATE INDEX IF NOT EXISTS ix_fixture_gameweek_assignments_gameweek
    ON app.fixture_gameweek_assignments (gameweek_id);

COMMENT ON TABLE app.fixture_gameweek_assignments IS
    'Insert-only. The current gameweek of a fixture is its newest row (highest id); older rows are the history of moves.';

-- 3. Backfill from the date windows the API used until now --------------------------------------
INSERT INTO app.fixture_gameweek_assignments (fixture_id, gameweek_id, reason)
SELECT f.id, g.id, 'Backfilled by V043 from the gameweek''s SAST date window'
FROM football.fixtures f
JOIN app.gameweeks g
  ON g.season_id = f.season_id
 AND (f.starting_at AT TIME ZONE 'Africa/Johannesburg')::date BETWEEN g.start_date AND g.end_date
WHERE NOT f.is_placeholder
  AND f.starting_at IS NOT NULL
  AND COALESCE(f.state_id, 1) NOT IN (10, 12, 15)
  AND NOT EXISTS (SELECT 1 FROM app.fixture_gameweek_assignments a WHERE a.fixture_id = f.id);

-- 4. Grants and row-level security ---------------------------------------------------------------
GRANT SELECT, INSERT ON app.fixture_gameweek_assignments TO app_api_user;
GRANT USAGE, SELECT ON SEQUENCE app.fixture_gameweek_assignments_id_seq TO app_api_user;

ALTER TABLE app.fixture_gameweek_assignments ENABLE ROW LEVEL SECURITY;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies
                   WHERE schemaname = 'app' AND tablename = 'fixture_gameweek_assignments' AND policyname = 'api_full_access') THEN
        CREATE POLICY api_full_access ON app.fixture_gameweek_assignments
            FOR ALL TO app_api_user USING (true) WITH CHECK (true);
    END IF;
END $$;
