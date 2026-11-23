-- V028: Move news_cache into the Python-owned schema, and install default
--       privileges so future tables no longer need per-table grant patches.
--
-- Two problems this fixes:
--
--   1. Ownership violation. app.news_cache (V026) is read AND written by the
--      Python sync worker, but the `app` schema is ASP.NET-owned. The sync
--      worker writing there breaks the schema-ownership boundary the roles
--      exist to enforce (and the permission error it produced was that boundary
--      working as designed). Move the table to `sync`, which the worker owns.
--
--   2. Missing grants on late-created tables. news_cache was created (V026)
--      AFTER V019's `GRANT ... ON ALL TABLES` ran, so the service roles never
--      received grants on it — the same pitfall V027 patched for player_lineups.
--      Instead of patching each new table forever, install ALTER DEFAULT
--      PRIVILEGES so every future table/sequence created by the Flyway admin
--      inherits the correct grants automatically.

-- ---------------------------------------------------------------------------
-- 1. Move the table (its data, owned sequence, and indexes travel with it).
-- ---------------------------------------------------------------------------
ALTER TABLE app.news_cache SET SCHEMA sync;

-- ---------------------------------------------------------------------------
-- 2. Explicit grants on the moved table + sequence.
--    Mirrors the V019 model: sync worker read/write, API read-only. No DELETE
--    (the cache overwrites expired rows via ON CONFLICT DO UPDATE).
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON sync.news_cache TO sync_worker_user;
GRANT USAGE, SELECT ON SEQUENCE sync.news_cache_id_seq TO sync_worker_user;
GRANT SELECT ON sync.news_cache TO app_api_user;

-- ---------------------------------------------------------------------------
-- 3. Default privileges — applied for objects created by the current (admin)
--    role, which is the role Flyway uses to create every table. This makes all
--    FUTURE tables/sequences inherit grants automatically, ending the
--    V027/step-2 patch cycle. Access model is identical to V019.
-- ---------------------------------------------------------------------------

-- football: sync worker read/write, API read-only
ALTER DEFAULT PRIVILEGES IN SCHEMA football
    GRANT SELECT, INSERT, UPDATE ON TABLES TO sync_worker_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA football
    GRANT USAGE, SELECT ON SEQUENCES TO sync_worker_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA football
    GRANT SELECT ON TABLES TO app_api_user;

-- sync: sync worker read/write, API read-only
ALTER DEFAULT PRIVILEGES IN SCHEMA sync
    GRANT SELECT, INSERT, UPDATE ON TABLES TO sync_worker_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA sync
    GRANT USAGE, SELECT ON SEQUENCES TO sync_worker_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA sync
    GRANT SELECT ON TABLES TO app_api_user;

-- app: API read/write. The sync worker intentionally gets NO schema-wide grant
--      here; its only app access stays the narrow SELECT on app.gameweeks (V019).
ALTER DEFAULT PRIVILEGES IN SCHEMA app
    GRANT SELECT, INSERT, UPDATE ON TABLES TO app_api_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA app
    GRANT USAGE, SELECT ON SEQUENCES TO app_api_user;
