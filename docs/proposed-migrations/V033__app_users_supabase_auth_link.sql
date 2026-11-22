-- PROPOSED — not applied. Copy into touchline-sync/db/migrations/ once the team agrees,
-- because Flyway in the sync repo owns all DDL (BACKEND_INTEGRATION.md §11).
--
-- V033: link app.users to Supabase Auth.
--
-- Why: the fantasy API verifies Supabase access tokens, whose "sub" claim is a UUID.
-- app.users (V014) has an integer id and a required password_hash, which assumes the
-- API stores passwords itself. With Supabase Auth holding credentials, the API needs
-- a column to find the app.users row for a token, and must be able to insert users
-- without a password.
--
-- Closes, if adopted: Touchline vault Open Questions #7 and #9 (app.users kept as the
-- profile table; Supabase Auth is the identity source).

ALTER TABLE app.users
    ADD COLUMN auth_user_id UUID;

ALTER TABLE app.users
    ADD CONSTRAINT uq_users_auth_user_id UNIQUE (auth_user_id);

ALTER TABLE app.users
    ALTER COLUMN password_hash DROP NOT NULL;

COMMENT ON COLUMN app.users.auth_user_id IS
    'Supabase Auth user id (auth.users.id / JWT sub). Set by the API on first sign-in.';

-- Optional, decide separately: a foreign key to auth.users(id). It keeps orphans out,
-- but ties app.users to Supabase internals and needs REFERENCES on auth.users.
-- ALTER TABLE app.users
--     ADD CONSTRAINT fk_users_auth_user
--     FOREIGN KEY (auth_user_id) REFERENCES auth.users(id);
