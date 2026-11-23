CREATE TABLE app.fantasy_teams (
    id              SERIAL PRIMARY KEY,
    user_id         INTEGER NOT NULL REFERENCES app.users(id),
    season_id       INTEGER NOT NULL REFERENCES football.seasons(id),
    name            VARCHAR(100) NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(user_id, season_id)
);

CREATE TRIGGER trg_fantasy_teams_updated_at
BEFORE UPDATE ON app.fantasy_teams
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
