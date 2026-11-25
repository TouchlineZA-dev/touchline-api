-- 001: give the API's database role access to the player price tables.
--
-- What:       GRANTs on app.player_prices and app.player_price_changes for app_api_user.
--             player_price_changes is append-only history, so it gets no UPDATE. No DELETE anywhere.
-- Why:        app_api_user had no privileges on either table, so GET /api/v1/players failed with
--             "permission denied for table player_prices" (seen 2026-11-22).
-- Depends on: the existing app.player_prices and app.player_price_changes tables.
-- How:        applied by hand in the Supabase SQL Editor by an owner account (postgres), in
--             number order (001, 002, 003, 004). The API's own role can't run it.
-- Re-running: safe. GRANT can be repeated.

GRANT SELECT, INSERT, UPDATE ON app.player_prices TO app_api_user;
GRANT SELECT, INSERT ON app.player_price_changes TO app_api_user;

GRANT USAGE, SELECT ON SEQUENCE app.player_prices_id_seq, app.player_price_changes_id_seq TO app_api_user;
