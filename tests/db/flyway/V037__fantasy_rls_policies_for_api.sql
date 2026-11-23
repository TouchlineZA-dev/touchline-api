-- V037: let the API's role use the fantasy tables while row-level security is on.
--
-- Copied from touchline-api docs/migrations/005_rls_policies_for_api.sql, which was applied by hand
-- in the Supabase SQL Editor on 2026-11-22. Safe to rerun: ENABLE ROW LEVEL SECURITY is a no-op when
-- already on, and each policy is created only if pg_policies doesn't have it yet.
--
-- What:  keeps row-level security enabled on the seven tables from V035 and adds one policy per
--        table that lets app_api_user read and write rows. No other role gets a policy, so
--        anon/authenticated (Supabase Data API) still see nothing if the app schema is exposed.
-- Why:   after the fantasy tables were created through the Supabase SQL Editor they had RLS enabled
--        with no policies. app_api_user doesn't bypass RLS, so its inserts failed ("new row violates
--        row-level security policy for table leagues") and its reads returned no rows. What
--        app_api_user may do is still limited by its GRANTs (SELECT, INSERT, UPDATE; no DELETE).
-- Needs: V035 (the seven tables).

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
