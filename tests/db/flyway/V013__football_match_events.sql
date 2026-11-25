-- player_id and related_player_id are raw integers, not FKs: match events may
-- reference players not yet present in football.players.
CREATE TABLE football.match_events (
    id                      SERIAL PRIMARY KEY,
    sportmonks_event_id     INTEGER NOT NULL UNIQUE,
    fixture_id              INTEGER NOT NULL REFERENCES football.fixtures(id),
    team_id                 INTEGER REFERENCES football.teams(id),
    player_id               INTEGER,
    related_player_id       INTEGER,
    player_name             VARCHAR(150),
    related_player_name     VARCHAR(150),
    type_id                 SMALLINT NOT NULL,
    minute                  SMALLINT,
    extra_minute            SMALLINT,
    result                  VARCHAR(10),
    addition                VARCHAR(100),
    sort_order               SMALLINT,
    rescinded                BOOLEAN,
    on_bench                 BOOLEAN NOT NULL DEFAULT FALSE,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_match_events_updated_at
BEFORE UPDATE ON football.match_events
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
