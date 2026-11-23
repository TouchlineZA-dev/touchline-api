CREATE TABLE app.player_prices (
    id              SERIAL PRIMARY KEY,
    player_id       INTEGER NOT NULL REFERENCES football.players(id),
    season_id       INTEGER NOT NULL REFERENCES football.seasons(id),
    price           NUMERIC(5,1) NOT NULL DEFAULT 50.0,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(player_id, season_id)
);

CREATE TRIGGER trg_player_prices_updated_at
BEFORE UPDATE ON app.player_prices
FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE INDEX idx_player_prices_season ON app.player_prices(season_id);
