-- Append-only: no updated_at, no update trigger
CREATE TABLE app.player_price_changes (
    id              SERIAL PRIMARY KEY,
    player_id       INTEGER NOT NULL REFERENCES football.players(id),
    season_id       INTEGER NOT NULL REFERENCES football.seasons(id),
    gameweek_id     INTEGER NOT NULL REFERENCES app.gameweeks(id),
    old_price       NUMERIC(5,1) NOT NULL,
    new_price       NUMERIC(5,1) NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_price_changes_player_season ON app.player_price_changes(player_id, season_id);
