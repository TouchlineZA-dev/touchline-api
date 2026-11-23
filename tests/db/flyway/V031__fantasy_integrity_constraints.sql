-- V031: fantasy integrity constraints + gameweek season scoping.
-- Coordinated with the ASP.NET side, which owns the `app` schema.
--
-- PRE-FLIGHT — if these tables already hold data, run each check first; every
-- one must return ZERO rows or the matching constraint below fails to create:
--
--   -- same player twice in a squad:
--   SELECT fantasy_team_id, gameweek_id, player_id, count(*)
--   FROM app.fantasy_squads GROUP BY 1,2,3 HAVING count(*) > 1;
--   -- two captains in one team's gameweek:
--   SELECT fantasy_team_id, gameweek_id, count(*)
--   FROM app.fantasy_squads WHERE is_captain GROUP BY 1,2 HAVING count(*) > 1;
--   -- two vice-captains:
--   SELECT fantasy_team_id, gameweek_id, count(*)
--   FROM app.fantasy_squads WHERE is_vice_captain GROUP BY 1,2 HAVING count(*) > 1;
--   -- duplicate gameweek status rows:
--   SELECT gameweek_id, count(*)
--   FROM sync.gameweek_sync_status GROUP BY 1 HAVING count(*) > 1;

-- 1. A player appears at most once in a team's squad for a given gameweek.
ALTER TABLE app.fantasy_squads
    ADD CONSTRAINT uq_fantasy_squads_team_gw_player
    UNIQUE (fantasy_team_id, gameweek_id, player_id);

-- 2. At most one captain and one vice-captain per team per gameweek.
--    Partial unique indexes are the idiomatic Postgres tool for "at most one X
--    per group": unlimited non-captains, a second captain rejected by the DB.
CREATE UNIQUE INDEX uq_fantasy_squads_one_captain
    ON app.fantasy_squads (fantasy_team_id, gameweek_id)
    WHERE is_captain;
CREATE UNIQUE INDEX uq_fantasy_squads_one_vice_captain
    ON app.fantasy_squads (fantasy_team_id, gameweek_id)
    WHERE is_vice_captain;

-- 3. Exactly one sync-status row per gameweek (the ASP.NET gate reads one row).
ALTER TABLE sync.gameweek_sync_status
    ADD CONSTRAINT uq_gameweek_sync_status_gameweek
    UNIQUE (gameweek_id);

-- 4. Scope gameweeks to a season so "Gameweek 1" doesn't collide the moment a
--    second season exists. Nullable pending backfill of existing rows; the
--    SET NOT NULL step is a queued follow-up once season_id is populated.
ALTER TABLE app.gameweeks
    ADD COLUMN season_id INTEGER REFERENCES football.seasons(id);
CREATE INDEX idx_gameweeks_season ON app.gameweeks (season_id);
