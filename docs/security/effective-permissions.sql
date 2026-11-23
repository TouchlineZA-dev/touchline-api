-- Effective permissions of the direct Supabase roles on every football / sync / app table.
-- Read only (catalog functions). Run in the Supabase SQL Editor or as any role that can read the catalog.
-- Expected (SPRINT_RULES rule 3, V019/V028/V037):
--   app_api_user      football/sync: SELECT only; app: SELECT, INSERT, UPDATE; never DELETE
--   sync_worker_user  football/sync: SELECT, INSERT, UPDATE; app: SELECT on gameweeks only
--   anon / authenticated (Supabase Data API): nothing on app; football/sync per the project's exposure decision
SELECT r.rolname AS role,
       t.schemaname AS schema,
       t.tablename AS table_name,
       has_table_privilege(r.rolname, format('%I.%I', t.schemaname, t.tablename), 'SELECT') AS can_select,
       has_table_privilege(r.rolname, format('%I.%I', t.schemaname, t.tablename), 'INSERT') AS can_insert,
       has_table_privilege(r.rolname, format('%I.%I', t.schemaname, t.tablename), 'UPDATE') AS can_update,
       has_table_privilege(r.rolname, format('%I.%I', t.schemaname, t.tablename), 'DELETE') AS can_delete,
       c.relrowsecurity AS rls_enabled
FROM pg_tables t
JOIN pg_class c ON c.relname = t.tablename AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t.schemaname)
CROSS JOIN pg_roles r
WHERE t.schemaname IN ('football', 'sync', 'app')
  AND r.rolname IN ('anon', 'authenticated', 'app_api_user', 'sync_worker_user')
ORDER BY role, schema, table_name;
