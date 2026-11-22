-- 005: let the API's role use the fantasy tables while row-level security is on.
--
-- What:       keeps row-level security (RLS) enabled on the seven tables from 003 and adds one policy
--             per table that lets app_api_user read and write rows. No other role gets a policy, so
--             anon/authenticated (Supabase Data API) still see nothing if the app schema is ever exposed.
-- Why:        after 003 ran, those tables had RLS enabled with no policies (the Supabase SQL Editor
--             enabled RLS on the new tables). app_api_user doesn't bypass RLS, so inserts failed with
--             "new row violates row-level security policy for table leagues" and reads returned no rows.
--             What app_api_user may do is still limited by the GRANTs (SELECT, INSERT, UPDATE; no DELETE).
-- Depends on: 003.
-- How:        applied by hand in the Supabase SQL Editor by an owner account (postgres), after 004.
-- Re-running: safe. ENABLE ROW LEVEL SECURITY is a no-op if already on; each policy is created only if missing.

DO $$
DECLARE
    t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'fantasy_gameweek_entries', 'fantasy_transfers', 'fantasy_chip_usage',
        'leagues', 'league_members', 'transfer_windows', 'player_price_overrides'
    ]
    LOOP
        EXECUTE format('ALTER TABLE app.%I ENABLE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname = 'app' AND tablename = t AND policyname = 'api_full_access') THEN
            EXECUTE format(
                'CREATE POLICY api_full_access ON app.%I FOR ALL TO app_api_user USING (true) WITH CHECK (true)', t);
        END IF;
    END LOOP;
END $$;
