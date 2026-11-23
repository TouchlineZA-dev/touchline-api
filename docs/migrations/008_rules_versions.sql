-- 008 (touchline-sync V044): remember which rules scored each entry.
--
-- Why: 03-Leruo-API Stage 2 — "a manager should later identify which rules were used to score it",
-- and a new rules version must never rescore the past. The API publishes the rulebook as version
-- "2026-27.v1" with points table "2026-27.points.v1" (GET /api/v1/rules).
--
-- What: nullable rules_version and points_table_version on app.fantasy_gameweek_entries and
-- app.points_results. The API stamps both when it finalises a gameweek, and refuses to rescore an
-- entry stamped with a different rules version. Rows already finalised before this migration were
-- scored under the 2026-27.v1 rules, so they're backfilled with that version.
--
-- Safe to rerun: ADD COLUMN IF NOT EXISTS; the backfill only fills NULLs on scored rows.

ALTER TABLE app.fantasy_gameweek_entries ADD COLUMN IF NOT EXISTS rules_version        VARCHAR(30);
ALTER TABLE app.fantasy_gameweek_entries ADD COLUMN IF NOT EXISTS points_table_version VARCHAR(30);
ALTER TABLE app.points_results           ADD COLUMN IF NOT EXISTS rules_version        VARCHAR(30);
ALTER TABLE app.points_results           ADD COLUMN IF NOT EXISTS points_table_version VARCHAR(30);

UPDATE app.fantasy_gameweek_entries
SET rules_version = '2026-27.v1', points_table_version = '2026-27.points.v1'
WHERE scored_at IS NOT NULL AND rules_version IS NULL;

UPDATE app.points_results r
SET rules_version = '2026-27.v1', points_table_version = '2026-27.points.v1'
WHERE r.rules_version IS NULL;

COMMENT ON COLUMN app.fantasy_gameweek_entries.rules_version IS
    'Rulebook version the entry was scored under (GET /api/v1/rules). Set when the gameweek is finalised; never changed by a later rules version.';
COMMENT ON COLUMN app.points_results.rules_version IS
    'Rulebook version these points were calculated under.';
