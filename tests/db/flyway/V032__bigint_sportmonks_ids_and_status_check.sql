-- V032: widen Sportmonks IDs to BIGINT (L1) + constrain sync_jobs.status (L2).

-- ---------------------------------------------------------------------------
-- L1. Sportmonks IDs are global auto-increments across their whole platform —
-- event and stat ids are on the same int4-overflow (2,147,483,647) trajectory
-- that already pushed lineup ids past int4 in the wild. Widen every remaining
-- sportmonks_*_id column (lossless int4 -> int8; table rewrite is seconds at our
-- volume, indexes rebuild automatically). Internal SERIAL PKs stay int4 — we
-- control those. player_lineups.sportmonks_lineup_id is already BIGINT (V021).
-- ---------------------------------------------------------------------------
ALTER TABLE football.competitions       ALTER COLUMN sportmonks_competition_id     TYPE BIGINT;
ALTER TABLE football.seasons            ALTER COLUMN sportmonks_season_id          TYPE BIGINT;
ALTER TABLE football.teams              ALTER COLUMN sportmonks_team_id            TYPE BIGINT;
ALTER TABLE football.players            ALTER COLUMN sportmonks_player_id          TYPE BIGINT;
ALTER TABLE football.fixtures           ALTER COLUMN sportmonks_fixture_id         TYPE BIGINT;
ALTER TABLE football.match_events       ALTER COLUMN sportmonks_event_id           TYPE BIGINT;
ALTER TABLE football.match_events       ALTER COLUMN sportmonks_player_id          TYPE BIGINT;
ALTER TABLE football.match_events       ALTER COLUMN sportmonks_related_player_id  TYPE BIGINT;
ALTER TABLE football.player_match_stats ALTER COLUMN sportmonks_stat_id            TYPE BIGINT;
ALTER TABLE football.player_lineups     ALTER COLUMN sportmonks_player_id          TYPE BIGINT;

-- ---------------------------------------------------------------------------
-- L2. sync_jobs.status was free text — a typo like 'succes' would silently and
-- permanently break the concurrency guard's status='running' logic. Constrain
-- to the known set. 'unknown' is included for V029's backfilled history rows.
-- ---------------------------------------------------------------------------
ALTER TABLE sync.sync_jobs
    ADD CONSTRAINT chk_sync_jobs_status
    CHECK (status IN ('running', 'success', 'failed', 'unknown'));
