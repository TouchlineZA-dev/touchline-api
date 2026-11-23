-- 009 (touchline-sync V045): account and team onboarding (API Sprint 3).
--
-- Why: 03-Leruo-API Stage 3 / handbook Gate 5. The verified Supabase subject is the permanent
-- account key, so email becomes optional (Apple relay, phone sign-in, Facebook without an email).
-- Managers choose a public username (3–20 letters, digits, underscore; unique ignoring case and
-- Unicode width), can rename their team, pick a favourite club, set notification preferences,
-- export and delete (anonymise) their account, and an admin can suspend an account.
--
-- What:
--   1. app.users: email nullable; username_canonical (NFKC + case-folded, unique), username_changed_at,
--      favourite_club_id (football.teams.id, no FK: the worker owns football rows), suspended_at,
--      suspension_reason, deleted_at (anonymised accounts; rows are never deleted).
--   2. app.username_history and app.fantasy_team_name_history: insert-only rename history for moderation.
--   3. app.notification_preferences: one row per user.
--   4. Grants (SELECT, INSERT, UPDATE; never DELETE) and RLS policies for app_api_user, as in V037.
--
-- Safe to rerun: IF NOT EXISTS everywhere; DROP NOT NULL is idempotent; policies created only if missing.

-- 1. app.users ------------------------------------------------------------------------------
ALTER TABLE app.users ALTER COLUMN email DROP NOT NULL;
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS username_canonical  VARCHAR(20);
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS username_changed_at TIMESTAMPTZ;
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS favourite_club_id   INTEGER;
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS suspended_at        TIMESTAMPTZ;
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS suspension_reason   TEXT;
ALTER TABLE app.users ADD COLUMN IF NOT EXISTS deleted_at          TIMESTAMPTZ;

CREATE UNIQUE INDEX IF NOT EXISTS uq_users_username_canonical
    ON app.users (username_canonical) WHERE username_canonical IS NOT NULL;

COMMENT ON COLUMN app.users.username_canonical IS
    'NFKC-normalised, case-folded username. Unique. NULL until the manager chooses a username (generated user_… names have none).';
COMMENT ON COLUMN app.users.deleted_at IS
    'Account deleted at the manager''s request: personal data anonymised, row and historical scores kept. auth_user_id is kept so the old login gets 403, not a new account.';

-- 2. Rename history (insert-only) -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS app.username_history (
    id            SERIAL PRIMARY KEY,
    user_id       INTEGER     NOT NULL REFERENCES app.users (id),
    old_username  VARCHAR(50) NOT NULL,
    new_username  VARCHAR(50) NOT NULL,
    changed_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_username_history_user ON app.username_history (user_id, id DESC);

CREATE TABLE IF NOT EXISTS app.fantasy_team_name_history (
    id                  SERIAL PRIMARY KEY,
    fantasy_team_id     INTEGER      NOT NULL REFERENCES app.fantasy_teams (id),
    old_name            VARCHAR(100) NOT NULL,
    new_name            VARCHAR(100) NOT NULL,
    changed_by_user_id  INTEGER      REFERENCES app.users (id),
    changed_at          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_team_name_history_team ON app.fantasy_team_name_history (fantasy_team_id, id DESC);

-- 3. Notification preferences ----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS app.notification_preferences (
    user_id             INTEGER     PRIMARY KEY REFERENCES app.users (id),
    goal_alerts         BOOLEAN     NOT NULL DEFAULT TRUE,
    deadline_reminders  BOOLEAN     NOT NULL DEFAULT TRUE,
    league_updates      BOOLEAN     NOT NULL DEFAULT TRUE,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- 4. Grants and row-level security --------------------------------------------------------------
GRANT SELECT, INSERT ON app.username_history, app.fantasy_team_name_history TO app_api_user;
GRANT SELECT, INSERT, UPDATE ON app.notification_preferences TO app_api_user;
GRANT USAGE, SELECT ON SEQUENCE app.username_history_id_seq, app.fantasy_team_name_history_id_seq TO app_api_user;

DO $$
DECLARE
    t TEXT;
BEGIN
    FOREACH t IN ARRAY ARRAY['username_history', 'fantasy_team_name_history', 'notification_preferences'] LOOP
        EXECUTE format('ALTER TABLE app.%I ENABLE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname = 'app' AND tablename = t AND policyname = 'api_full_access') THEN
            EXECUTE format('CREATE POLICY api_full_access ON app.%I FOR ALL TO app_api_user USING (true) WITH CHECK (true)', t);
        END IF;
    END LOOP;
END $$;
