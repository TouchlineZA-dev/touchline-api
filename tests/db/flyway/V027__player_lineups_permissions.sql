-- football.player_lineups was created in V021, after the V019 blanket grant ran.
-- PostgreSQL's ON ALL TABLES IN SCHEMA only covers tables that exist at grant time,
-- so sync_worker_user and app_api_user missed this table. Grant explicitly here.
GRANT SELECT, INSERT, UPDATE ON football.player_lineups TO sync_worker_user;
GRANT USAGE, SELECT ON SEQUENCE football.player_lineups_id_seq TO sync_worker_user;

GRANT SELECT ON football.player_lineups TO app_api_user;
