CREATE TABLE football.fixtures (
    id                      SERIAL PRIMARY KEY,
    sportmonks_fixture_id   INTEGER NOT NULL UNIQUE,
    competition_id          INTEGER NOT NULL REFERENCES football.competitions(id),
    season_id               INTEGER NOT NULL REFERENCES football.seasons(id),
    stage_id                INTEGER,
    aggregate_id            INTEGER,
    venue_id                INTEGER,
    state_id                SMALLINT,
    home_team_id            INTEGER REFERENCES football.teams(id),
    away_team_id            INTEGER REFERENCES football.teams(id),
    home_score              SMALLINT,
    away_score              SMALLINT,
    home_penalty_score      SMALLINT,
    away_penalty_score      SMALLINT,
    starting_at             TIMESTAMPTZ,
    result_info             TEXT,
    leg                     VARCHAR(5),
    length                  SMALLINT,
    is_placeholder          BOOLEAN NOT NULL DEFAULT FALSE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_fixtures_updated_at
BEFORE UPDATE ON football.fixtures
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
