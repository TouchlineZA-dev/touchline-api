-- V033: link app.users to Supabase Auth.
--
-- Why: touchline-api verifies Supabase access tokens, whose "sub" claim is a UUID.
-- app.users (V014) has an integer id and a required password_hash, which assumes the
-- API stores passwords itself. With Supabase Auth holding credentials, the API needs
-- a column to find the app.users row for a token, and must be able to insert users
-- without a password.
--
-- Fantasy tables keep referencing app.users.id (integer). Only app.users knows the
-- Supabase UUID.
--
-- Same as touchline-api docs/migrations/002_users_auth_link.sql (applied by hand to the shared
-- database), plus a case-insensitive username index.
--
-- Safe to rerun on a database that already has these changes: the column and index use
-- IF NOT EXISTS, the constraint is added only if pg_constraint lacks it, and DROP NOT NULL /
-- COMMENT are no-ops when repeated.

ALTER TABLE app.users ADD COLUMN IF NOT EXISTS auth_user_id UUID;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_users_auth_user_id' AND conrelid = 'app.users'::regclass) THEN
        ALTER TABLE app.users ADD CONSTRAINT uq_users_auth_user_id UNIQUE (auth_user_id);
    END IF;
END $$;

ALTER TABLE app.users ALTER COLUMN password_hash DROP NOT NULL;

-- "Funi" and "funi" must not both exist. The V014 UNIQUE(username) is case-sensitive,
-- so add a case-insensitive one. If this fails, existing rows already clash; find them with
--   SELECT lower(username), count(*) FROM app.users GROUP BY 1 HAVING count(*) > 1;
CREATE UNIQUE INDEX IF NOT EXISTS uq_users_username_lower ON app.users (lower(username));

COMMENT ON COLUMN app.users.auth_user_id IS
    'Supabase Auth user id (auth.users.id / JWT sub). Set by touchline-api on first sign-in.';

-- Not added on purpose: a foreign key to auth.users(id). It would tie app.users to
-- Supabase internals, and touchline-api keeps auth_user_id on deactivated rows after the
-- Supabase login is deleted (so old tokens get a 403 instead of creating a new account).
