CREATE TABLE app.points_results (
    id                  SERIAL PRIMARY KEY,
    fantasy_team_id     INTEGER NOT NULL REFERENCES app.fantasy_teams(id),
    player_id           INTEGER NOT NULL REFERENCES football.players(id),
    gameweek_id         INTEGER NOT NULL REFERENCES app.gameweeks(id),
    points              INTEGER NOT NULL DEFAULT 0,
    breakdown           JSONB,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_points_results_updated_at
BEFORE UPDATE ON app.points_results
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
