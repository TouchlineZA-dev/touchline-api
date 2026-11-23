CREATE TABLE sync.gameweek_sync_status (
    id                  SERIAL PRIMARY KEY,
    gameweek_id         INTEGER NOT NULL REFERENCES app.gameweeks(id),
    fixtures_complete   BOOLEAN NOT NULL DEFAULT FALSE,
    events_complete     BOOLEAN NOT NULL DEFAULT FALSE,
    stats_complete      BOOLEAN NOT NULL DEFAULT FALSE,
    marked_complete     BOOLEAN NOT NULL DEFAULT FALSE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_gameweek_sync_status_updated_at
BEFORE UPDATE ON sync.gameweek_sync_status
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
