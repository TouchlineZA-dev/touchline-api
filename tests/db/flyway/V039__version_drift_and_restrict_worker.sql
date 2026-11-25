-- V039: version the production drift found in Sprint 1/2, and restrict
-- sync_worker_user to the football and sync data it owns.
--
-- Safe on both a clean install (V001-V038) and production (which already has
-- the drifted objects): every step is IF NOT EXISTS / idempotent.
--
-- 1. football.player_match_stats.player_id + idx_player_match_stats_player
--    exist in production but no migration created them. Kept as-is (nullable,
--    no FK, all NULL today) and reserved for per-player statistics. Its final
--    shape depends on the Sprint 1 decision about player-level stats.
-- 2. football.standings (32 rows, last written 2026-07-25) exists in
--    production with no migration. Definition copied from production.
-- 3. Production grants sync_worker_user SELECT on app.users / fantasy_teams /
--    fantasy_squads and INSERT/UPDATE on app.points_results / leaderboards /
--    player_prices. Fantasy data belongs to touchline-api; the worker uses
--    none of it. Leave only SELECT on app.gameweeks (V019), which the
--    gameweek status writer needs.

-- 1 -------------------------------------------------------------------------
ALTER TABLE football.player_match_stats ADD COLUMN IF NOT EXISTS player_id INTEGER;
CREATE INDEX IF NOT EXISTS idx_player_match_stats_player
    ON football.player_match_stats (player_id);
COMMENT ON COLUMN football.player_match_stats.player_id IS
    'Reserved for per-player statistics; NULL = team-level row (all rows today).';

-- 2 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS football.standings (
    id                      SERIAL PRIMARY KEY,
    sportmonks_standing_id  BIGINT UNIQUE,
    season_id               INTEGER NOT NULL REFERENCES football.seasons (id),
    team_id                 INTEGER NOT NULL REFERENCES football.teams (id),
    position                SMALLINT,
    result                  VARCHAR(50),
    points                  SMALLINT,
    played                  SMALLINT,
    won                     SMALLINT,
    drawn                   SMALLINT,
    lost                    SMALLINT,
    goals_for               SMALLINT,
    goals_against           SMALLINT,
    goal_difference         SMALLINT,
    raw                     JSONB,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (season_id, team_id)
);
CREATE INDEX IF NOT EXISTS idx_standings_season ON football.standings (season_id);
CREATE INDEX IF NOT EXISTS idx_standings_team   ON football.standings (team_id);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'trg_standings_updated_at'
          AND tgrelid = 'football.standings'::regclass
    ) THEN
        CREATE TRIGGER trg_standings_updated_at
        BEFORE UPDATE ON football.standings
        FOR EACH ROW EXECUTE FUNCTION set_updated_at();
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON football.standings TO sync_worker_user;
GRANT USAGE, SELECT ON SEQUENCE football.standings_id_seq TO sync_worker_user;
GRANT SELECT ON football.standings TO app_api_user;

-- 3 -------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES    IN SCHEMA app FROM sync_worker_user;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA app FROM sync_worker_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA app REVOKE ALL ON TABLES    FROM sync_worker_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA app REVOKE ALL ON SEQUENCES FROM sync_worker_user;
GRANT SELECT ON app.gameweeks TO sync_worker_user;
