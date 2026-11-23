-- football.standings exists in the production database but no touchline-sync Flyway migration
-- creates it (checked against sync main 15b49d1 on 2026-11-23). This mirrors the live columns
-- (information_schema, read-only check on 2026-11-23) so the API's standings tests run on the same
-- shape. Needs sync team: add a Flyway migration for it, then delete this file.
CREATE TABLE IF NOT EXISTS football.standings (
    id                      SERIAL PRIMARY KEY,
    sportmonks_standing_id  BIGINT,
    season_id               INTEGER NOT NULL REFERENCES football.seasons(id),
    team_id                 INTEGER NOT NULL REFERENCES football.teams(id),
    position                SMALLINT,
    result                  VARCHAR(50),
    points                  SMALLINT,
    played                  SMALLINT,
    won                     SMALLINT,
    drawn                   SMALLINT,
    lost                    SMALLINT,
    goals_for               SMALLINT,
    goals_against           SMALLINT,
    goal_difference         SMALLINT,
    raw                     JSONB,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
GRANT SELECT ON football.standings TO app_api_user;
