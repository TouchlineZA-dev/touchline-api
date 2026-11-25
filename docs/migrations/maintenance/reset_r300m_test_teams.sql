-- MAINTENANCE: put test teams created with the scrapped R300m budget back on R100.0m. Not a migration; don't number it.
--
-- Why:    on 2026-11-25 a deploy ran with Fantasy:Budget = 300.0 (the value on the unmerged phathu-dev
--         branch, or a Fantasy__Budget variable on the host). Teams created then started with R300m.
--         A read-only check on 2026-11-25 (as app_api_user) found two, both test teams:
--           id 3 "KhanyaFC"  created 2026-11-25, bank 236.0, 15-player squad bought for 64.0 (total 300.0)
--           id 4 "lwdmkwe"   created 2026-11-25, bank 300.0, no squad                   (total 300.0)
--         No transfers on either. Teams 1 and 2 (2026-11-22) are correct at 100.0.
-- What:   for each team whose bank + latest squad's purchase cost is more than 100.0:
--           bank := 100.0 − that squad cost (100.0 for a team with no squad). Squads are kept.
--         Only app.fantasy_teams.budget_remaining is updated. No DELETE anywhere.
-- Squads that no longer fit (cost over 100.0): the script refuses and lists them, because clearing a
--         squad needs a DELETE on app.fantasy_squads, which this script must not do. None exist today.
-- How:    review, then run in the Supabase SQL Editor (app_api_user's UPDATE on app is enough).
--         It ends in ROLLBACK, so the first run is a dry run: the last table shows before/after.
--         Change ROLLBACK to COMMIT only after the "before" rows match the list above.
-- Safety: one transaction; stops unless the affected teams are exactly the reviewed ids {3, 4}.
--         Fix the deploy first (startup log must say "Fantasy budget: 100.0"), or new teams keep arriving.

BEGIN;

CREATE TEMP TABLE r300_target ON COMMIT DROP AS
WITH latest AS (
    SELECT DISTINCT ON (fantasy_team_id) fantasy_team_id, gameweek_id
    FROM app.fantasy_squads
    ORDER BY fantasy_team_id, gameweek_id DESC
), cost AS (
    SELECT s.fantasy_team_id, sum(s.purchase_price) AS squad_cost, count(*) FILTER (WHERE s.purchase_price IS NULL) AS unpriced
    FROM app.fantasy_squads s JOIN latest l USING (fantasy_team_id, gameweek_id)
    GROUP BY s.fantasy_team_id
)
SELECT t.id AS team_id, t.name, t.created_at, t.budget_remaining AS bank_before,
       coalesce(c.squad_cost, 0) AS squad_cost, coalesce(c.unpriced, 0) AS unpriced
FROM app.fantasy_teams t
LEFT JOIN cost c ON c.fantasy_team_id = t.id
WHERE t.budget_remaining + coalesce(c.squad_cost, 0) > 100.0;

DO $$
DECLARE ids int[]; over text;
BEGIN
    SELECT array_agg(team_id ORDER BY team_id) INTO ids FROM r300_target;
    IF ids IS DISTINCT FROM ARRAY[3, 4] THEN
        RAISE EXCEPTION 'Expected teams {3,4}, found %. Re-review before running.', coalesce(ids::text, '{}');
    END IF;
    SELECT string_agg(team_id || ' (' || squad_cost || ')', ', ') INTO over FROM r300_target WHERE squad_cost > 100.0 OR unpriced > 0;
    IF over IS NOT NULL THEN
        RAISE EXCEPTION 'Squads that no longer fit R100.0m or have unpriced picks: %. Needs a decision (clearing needs DELETE).', over;
    END IF;
END $$;

UPDATE app.fantasy_teams t
SET budget_remaining = 100.0 - r.squad_cost, updated_at = now()
FROM r300_target r
WHERE t.id = r.team_id;

SELECT r.team_id, r.name, r.created_at::date AS created, r.squad_cost, r.bank_before, t.budget_remaining AS bank_after,
       r.squad_cost + t.budget_remaining AS total_after
FROM r300_target r JOIN app.fantasy_teams t ON t.id = r.team_id
ORDER BY r.team_id;

ROLLBACK;   -- dry run. Change to COMMIT after review.
