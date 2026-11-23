-- sync_worker_user: full access to football and sync
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA football TO sync_worker_user;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA football TO sync_worker_user;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA sync TO sync_worker_user;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA sync TO sync_worker_user;

-- sync_worker_user: SELECT on app.gameweeks ONLY
-- Required so Python can validate gameweek_id FK when writing sync.gameweek_sync_status
GRANT SELECT ON app.gameweeks TO sync_worker_user;

-- app_api_user: read only on football and sync
GRANT SELECT ON ALL TABLES IN SCHEMA football TO app_api_user;
GRANT SELECT ON ALL TABLES IN SCHEMA sync TO app_api_user;

-- app_api_user: full access to app schema
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA app TO app_api_user;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA app TO app_api_user;
