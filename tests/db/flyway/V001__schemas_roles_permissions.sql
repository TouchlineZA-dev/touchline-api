-- Schemas
CREATE SCHEMA IF NOT EXISTS football;
CREATE SCHEMA IF NOT EXISTS sync;
CREATE SCHEMA IF NOT EXISTS app;

-- Schemas
CREATE SCHEMA IF NOT EXISTS football;
CREATE SCHEMA IF NOT EXISTS sync;
CREATE SCHEMA IF NOT EXISTS app;

-- Users with passwords
CREATE USER sync_worker_user WITH PASSWORD '<redacted>';
CREATE USER app_api_user WITH PASSWORD '<redacted>';

-- Schema usage grants
GRANT USAGE ON SCHEMA football TO sync_worker_user;
GRANT USAGE ON SCHEMA sync TO sync_worker_user;
GRANT USAGE ON SCHEMA app TO sync_worker_user;

GRANT USAGE ON SCHEMA football TO app_api_user;
GRANT USAGE ON SCHEMA sync TO app_api_user;
GRANT USAGE ON SCHEMA app TO app_api_user;