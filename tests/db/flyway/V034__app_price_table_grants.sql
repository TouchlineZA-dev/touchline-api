-- V034: give app_api_user access to the player price tables.
--
-- Why: V019 granted app_api_user SELECT/INSERT/UPDATE on every app table that existed
-- then. app.player_prices (V022) and app.player_price_changes (V023) came later, and the
-- default privileges only arrived in V028, so app_api_user has no access to either. As a
-- result touchline-api's GET /api/v1/players fails with "permission denied for table
-- player_prices" (seen 2026-11-22).
--
-- player_price_changes is append-only history: no UPDATE. No DELETE anywhere.

GRANT SELECT, INSERT, UPDATE ON app.player_prices TO app_api_user;
GRANT SELECT, INSERT ON app.player_price_changes TO app_api_user;

GRANT USAGE, SELECT ON SEQUENCE app.player_prices_id_seq, app.player_price_changes_id_seq TO app_api_user;
