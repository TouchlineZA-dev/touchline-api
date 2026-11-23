CREATE TABLE football.player_match_stats (
    id                      SERIAL PRIMARY KEY,
    sportmonks_stat_id      INTEGER NOT NULL UNIQUE,
    fixture_id              INTEGER NOT NULL REFERENCES football.fixtures(id),
    team_id                 INTEGER REFERENCES football.teams(id),
    type_id                 SMALLINT NOT NULL,
    value                   INTEGER,
    location                VARCHAR(5),
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_player_match_stats_updated_at
BEFORE UPDATE ON football.player_match_stats
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
