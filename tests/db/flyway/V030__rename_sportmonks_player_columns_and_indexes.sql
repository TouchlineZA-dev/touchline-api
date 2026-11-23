-- V030: make the mixed-ID-space explicit in match_events / player_lineups,
--       and add the indexes the points-calculation + gameweek-window queries need.
--
-- In both tables fixture_id and team_id are INTERNAL db ids, while the player
-- references are SPORTMONKS ids (deliberate — events can reference players not
-- yet synced, per V013). The bare name `player_id` invited the ASP.NET points
-- calculator to FK-join it against football.players.id, which is wrong. Rename
-- so the id space is unmistakable. Renames land now, before ASP.NET builds on
-- these tables (they get exponentially harder afterward).
--
-- Only OUR column names change. Sportmonks payloads still use "player_id"; the
-- mapper's raw-side reads are unchanged.

ALTER TABLE football.match_events   RENAME COLUMN player_id         TO sportmonks_player_id;
ALTER TABLE football.match_events   RENAME COLUMN related_player_id TO sportmonks_related_player_id;
ALTER TABLE football.player_lineups RENAME COLUMN player_id         TO sportmonks_player_id;

-- Access paths: points calc joins events/lineups by sportmonks player id;
-- gameweek/window queries scan fixtures by kickoff time.
CREATE INDEX IF NOT EXISTS idx_match_events_sportmonks_player
    ON football.match_events (sportmonks_player_id);
CREATE INDEX IF NOT EXISTS idx_player_lineups_sportmonks_player
    ON football.player_lineups (sportmonks_player_id);
CREATE INDEX IF NOT EXISTS idx_player_lineups_fixture
    ON football.player_lineups (fixture_id);
CREATE INDEX IF NOT EXISTS idx_fixtures_starting_at
    ON football.fixtures (starting_at);
