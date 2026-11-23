CREATE TABLE app.gameweeks (
    id              SERIAL PRIMARY KEY,
    name            VARCHAR(50) NOT NULL,
    start_date      DATE NOT NULL,
    end_date        DATE NOT NULL,
    is_active       BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_gameweeks_updated_at
BEFORE UPDATE ON app.gameweeks
FOR EACH ROW EXECUTE FUNCTION set_updated_at();
