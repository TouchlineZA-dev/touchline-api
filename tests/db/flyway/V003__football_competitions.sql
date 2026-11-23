CREATE TABLE football.competitions (
    id                          SERIAL PRIMARY KEY,
    sportmonks_competition_id  INTEGER NOT NULL UNIQUE,
    name                        VARCHAR(150) NOT NULL,
    short_code                  VARCHAR(20),
    country_id                  INTEGER,
    type                         VARCHAR(30),
    sub_type                    VARCHAR(30),
    active                       BOOLEAN NOT NULL DEFAULT TRUE,
    image_path                  TEXT,
    category                    SMALLINT,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_competitions_updated_at
BEFORE UPDATE ON football.competitions
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
