CREATE TABLE football.players (
    id                      SERIAL PRIMARY KEY,
    sportmonks_player_id    INTEGER NOT NULL UNIQUE,
    firstname               VARCHAR(100),
    lastname                VARCHAR(100),
    display_name            VARCHAR(150),
    common_name             VARCHAR(100),
    country_id              INTEGER,
    nationality_id          INTEGER,
    position_id             SMALLINT,
    detailed_position_id    SMALLINT,
    date_of_birth           DATE,
    height                  SMALLINT,
    weight                  SMALLINT,
    image_path              TEXT,
    gender                  VARCHAR(10),
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_players_updated_at
BEFORE UPDATE ON football.players
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
