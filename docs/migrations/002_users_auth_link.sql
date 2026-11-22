-- 002: link app.users to Supabase Auth.
--
-- What:       adds app.users.auth_user_id (UUID, unique), makes password_hash nullable, and adds a
--             case-insensitive unique index on username ("Funi" and "funi" can't both exist).
-- Why:        touchline-api verifies Supabase access tokens, whose "sub" claim is a UUID. app.users has
--             an integer id and a required password_hash, which assumes the API stores passwords
--             itself. Supabase Auth holds the credentials, so the API needs a column to find the
--             app.users row for a token, and must be able to insert users without a password.
--             Fantasy tables keep referencing app.users.id (integer).
-- Depends on: the existing app.users table.
-- How:        applied by hand in the Supabase SQL Editor by an owner account (postgres), after 001.
-- Re-running: safe. Every statement checks first.
-- Mirror:     touchline-sync db/migrations/V033 (same SQL). The username index was added to this file
--             on 2026-11-22 to match V033; the shared database doesn't have it yet, so it is created
--             the next time this file (or V033) runs.

ALTER TABLE app.users ADD COLUMN IF NOT EXISTS auth_user_id UUID;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_users_auth_user_id' AND conrelid = 'app.users'::regclass) THEN
        ALTER TABLE app.users ADD CONSTRAINT uq_users_auth_user_id UNIQUE (auth_user_id);
    END IF;
END $$;

-- Running DROP NOT NULL on a column that is already nullable is a no-op.
ALTER TABLE app.users ALTER COLUMN password_hash DROP NOT NULL;

-- V014's UNIQUE(username) is case-sensitive. Fails only if usernames already clash; find them with
--   SELECT lower(username), count(*) FROM app.users GROUP BY 1 HAVING count(*) > 1;
CREATE UNIQUE INDEX IF NOT EXISTS uq_users_username_lower ON app.users (lower(username));

COMMENT ON COLUMN app.users.auth_user_id IS
    'Supabase Auth user id (auth.users.id / JWT sub). Set by touchline-api on first sign-in.';

-- Not added on purpose: a foreign key to auth.users(id). It would tie app.users to Supabase internals.
