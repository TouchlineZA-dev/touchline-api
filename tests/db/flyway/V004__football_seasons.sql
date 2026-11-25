CREATE TABLE football.seasons (
    id                      SERIAL PRIMARY KEY,
    sportmonks_season_id    INTEGER NOT NULL UNIQUE,
    competition_id          INTEGER NOT NULL REFERENCES football.competitions(id),
    name                    VARCHAR(20) NOT NULL,
    starting_at             DATE,
    ending_at               DATE,
    is_current              BOOLEAN NOT NULL DEFAULT FALSE,
    finished                BOOLEAN NOT NULL DEFAULT FALSE,
    pending                 BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_seasons_updated_at
BEFORE UPDATE ON football.seasons
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
