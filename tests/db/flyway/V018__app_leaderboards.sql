CREATE TABLE app.leaderboards (
    id                  SERIAL PRIMARY KEY,
    gameweek_id         INTEGER NOT NULL REFERENCES app.gameweeks(id),
    user_id             INTEGER NOT NULL REFERENCES app.users(id),
    fantasy_team_id     INTEGER NOT NULL REFERENCES app.fantasy_teams(id),
    total_points        INTEGER NOT NULL DEFAULT 0,
    rank                INTEGER,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_leaderboards_updated_at
BEFORE UPDATE ON app.leaderboards
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
