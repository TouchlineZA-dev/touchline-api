CREATE TABLE football.teams (
    id                      SERIAL PRIMARY KEY,
    sportmonks_team_id      INTEGER NOT NULL UNIQUE,
    name                    VARCHAR(150) NOT NULL,
    short_code              VARCHAR(20),
    country_id              INTEGER,
    venue_id                INTEGER,
    founded                 SMALLINT,
    type                    VARCHAR(30),
    image_path              TEXT,
    is_placeholder          BOOLEAN NOT NULL DEFAULT FALSE,
    last_played_at          TIMESTAMPTZ,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_teams_updated_at
BEFORE UPDATE ON football.teams
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
