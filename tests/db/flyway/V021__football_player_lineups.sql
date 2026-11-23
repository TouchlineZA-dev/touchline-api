CREATE TABLE football.player_lineups (
    id                      SERIAL PRIMARY KEY,
    sportmonks_lineup_id    BIGINT NOT NULL UNIQUE,
    fixture_id              INTEGER NOT NULL REFERENCES football.fixtures(id),
    team_id                 INTEGER REFERENCES football.teams(id),
    player_id               INTEGER,
    player_name             VARCHAR(150),
    jersey_number           SMALLINT,
    position_id             SMALLINT,
    type_id                 SMALLINT NOT NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_player_lineups_updated_at
BEFORE UPDATE ON football.player_lineups
FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_player_lineups_fixture ON football.player_lineups(fixture_id);