CREATE TABLE app.fantasy_squads (
    id                  SERIAL PRIMARY KEY,
    fantasy_team_id     INTEGER NOT NULL REFERENCES app.fantasy_teams(id),
    player_id           INTEGER NOT NULL REFERENCES football.players(id),
    gameweek_id         INTEGER NOT NULL REFERENCES app.gameweeks(id),
    is_captain          BOOLEAN NOT NULL DEFAULT FALSE,
    is_vice_captain     BOOLEAN NOT NULL DEFAULT FALSE,
    is_on_bench         BOOLEAN NOT NULL DEFAULT FALSE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_fantasy_squads_updated_at
BEFORE UPDATE ON app.fantasy_squads
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
