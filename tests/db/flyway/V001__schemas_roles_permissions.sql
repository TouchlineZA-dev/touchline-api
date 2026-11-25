-- Schemas
CREATE SCHEMA IF NOT EXISTS football;
CREATE SCHEMA IF NOT EXISTS sync;
CREATE SCHEMA IF NOT EXISTS app;

-- Schemas
CREATE SCHEMA IF NOT EXISTS football;
CREATE SCHEMA IF NOT EXISTS sync;
CREATE SCHEMA IF NOT EXISTS app;

-- Login roles, created WITHOUT a password: neither can sign in until an admin
-- sets one with scripts/rotate_role_passwords.py (or ALTER ROLE ... PASSWORD).
-- Edited 24 Nov 2026 to remove passwords that had been committed here. The
-- checksum change needs a one-off `./migrate.sh repair` on every database that
-- already applied V001; the passwords it used to set must be treated as public
-- and rotated wherever they are still in use.
CREATE USER sync_worker_user;
CREATE USER app_api_user;

-- Schema usage grants
GRANT USAGE ON SCHEMA football TO sync_worker_user;
GRANT USAGE ON SCHEMA sync TO sync_worker_user;
GRANT USAGE ON SCHEMA app TO sync_worker_user;

GRANT USAGE ON SCHEMA football TO app_api_user;
GRANT USAGE ON SCHEMA sync TO app_api_user;
GRANT USAGE ON SCHEMA app TO app_api_user;