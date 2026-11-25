-- V050: fixes and handoff for the API's Sprint 9 (24 Nov 2026). Additive; replaces functions only.
--
-- 1. The worker may read app.fixture_gameweek_assignments (V043): grant plus an RLS
--    SELECT policy (RLS is on, and V043's only policy is for app_api_user).
-- 2. sync.fixture_gameweek(fixture_id): the fixture's current gameweek is its newest
--    V043 assignment row (NULL = deliberately unassigned). Only a fixture with no
--    assignment row at all falls back to the SAST date window (sync.gameweek_for).
-- 3. The reschedule trigger records the ASSIGNED gameweek as old_gameweek_id. As
--    new_gameweek_id it records where the date rule would put the new kickoff, or
--    NULL when the fixture became unplayable (postponed, cancelled, TBA, abandoned,
--    deleted). The trigger never writes assignments: moving a fixture between
--    gameweeks stays the API's decision.
-- 4. assess_fixture rule version 2:
--    - gameweek from sync.fixture_gameweek()
--    - event type ids corrected: Sportmonks has 14 goal, 15 own goal, 16 penalty.
--      V041 required a scorer only for 14 and 15, so an unattributed PENALTY could pass
--      as complete. Now every goal type needs a player id.
--    - goals the provider marks rescinded never count towards the score check
--    Postponed (10), cancelled (12), TBA (13) and deleted (20) fixtures are still
--    'excluded', as in V041: never 'incomplete', never blocking a gameweek.
-- 5. sync.gameweek_completeness: one row per gameweek, rolled up from
--    sync.fixture_completeness over the assigned fixtures. It replaces
--    sync.gameweek_sync_status, which only the Python worker writes, and production
--    has always been written by Netlify (hence 0 rows there).
--
-- Existing completeness rows keep rule_version 1 until the fixture is next observed.
-- To re-assess every fixture at once (admin, after migrating):
--   SELECT sync.assess_fixture(fixture_id, now(), revision) FROM sync.fixture_completeness;

-- 1 ---- worker reads gameweek assignments -------------------------------------
GRANT SELECT ON app.fixture_gameweek_assignments TO sync_worker_user;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies
                   WHERE schemaname = 'app' AND tablename = 'fixture_gameweek_assignments'
                     AND policyname = 'sync_worker_read') THEN
        CREATE POLICY sync_worker_read ON app.fixture_gameweek_assignments
            FOR SELECT TO sync_worker_user USING (true);
    END IF;
END $$;

-- 2 ---- current gameweek of a fixture -----------------------------------------
CREATE OR REPLACE FUNCTION sync.fixture_gameweek(p_fixture_id INTEGER)
RETURNS INTEGER LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN a.fixture_id IS NOT NULL THEN a.gameweek_id
                ELSE sync.gameweek_for(f.season_id, f.starting_at) END
    FROM football.fixtures f
    LEFT JOIN LATERAL (
        SELECT fixture_id, gameweek_id FROM app.fixture_gameweek_assignments
        WHERE fixture_id = f.id ORDER BY id DESC LIMIT 1
    ) a ON true
    WHERE f.id = p_fixture_id
$$;
COMMENT ON FUNCTION sync.fixture_gameweek(INTEGER) IS
    'Current gameweek: newest app.fixture_gameweek_assignments row (NULL = unassigned); '
    'the SAST date window only when the fixture has never been assigned.';
REVOKE ALL ON FUNCTION sync.fixture_gameweek(INTEGER) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.fixture_gameweek(INTEGER) TO sync_worker_user, app_api_user;

-- 3 ---- reschedule history uses the assignment --------------------------------
CREATE OR REPLACE FUNCTION sync.log_fixture_schedule_change()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.starting_at IS DISTINCT FROM OLD.starting_at
       OR (NEW.state_id IS DISTINCT FROM OLD.state_id
           AND (coalesce(OLD.state_id, 1) IN (10, 12, 13, 14, 15, 16, 17, 20)
                OR coalesce(NEW.state_id, 1) IN (10, 12, 13, 14, 15, 16, 17, 20)))
    THEN
        INSERT INTO sync.fixture_schedule_changes
            (fixture_id, sportmonks_fixture_id, old_starting_at, new_starting_at,
             old_state_id, new_state_id, old_gameweek_id, new_gameweek_id, changed_at)
        VALUES
            (NEW.id, NEW.sportmonks_fixture_id, OLD.starting_at, NEW.starting_at,
             OLD.state_id, NEW.state_id,
             -- where the fixture is assigned now (the API has not moved it yet)
             CASE WHEN EXISTS (SELECT 1 FROM app.fixture_gameweek_assignments WHERE fixture_id = NEW.id)
                  THEN sync.fixture_gameweek(NEW.id)
                  ELSE sync.gameweek_for(OLD.season_id, OLD.starting_at) END,
             -- where the date rule would put it; NULL once it can't be played
             CASE WHEN coalesce(NEW.state_id, 1) IN (10, 12, 13, 15, 20) THEN NULL
                  ELSE sync.gameweek_for(NEW.season_id, NEW.starting_at) END,
             clock_timestamp());
    END IF;
    RETURN NULL;
END $$;
COMMENT ON COLUMN sync.fixture_schedule_changes.old_gameweek_id IS
    'Gameweek the fixture was assigned to when it changed (V043 assignment; date window if never assigned).';
COMMENT ON COLUMN sync.fixture_schedule_changes.new_gameweek_id IS
    'Gameweek the date rule gives the new kickoff, or NULL when postponed/cancelled/TBA/abandoned/deleted. '
    'A suggestion: the API decides and records the move in app.fixture_gameweek_assignments.';

-- 4 ---- completeness rule version 2 -------------------------------------------
CREATE OR REPLACE FUNCTION sync.assess_fixture(
    p_fixture_id INTEGER, p_at TIMESTAMPTZ, p_revision INTEGER)
RETURNS TEXT LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE
    f        football.fixtures%ROWTYPE;
    st       TEXT;
    why      TEXT;
    inputs   JSONB := '{}';
    issues   JSONB := '[]';
    hst INT; ast INT; unid INT; ev INT; hg INT; ag INT; nosc INT; stats INT;
    blocking INT;
    settle_until   TIMESTAMPTZ;
    deadline       TIMESTAMPTZ;
BEGIN
    SELECT * INTO f FROM football.fixtures WHERE id = p_fixture_id;
    settle_until := f.starting_at + interval '4 hours';
    deadline     := f.starting_at + interval '72 hours';

    IF f.state_id IN (10, 12, 13, 20) THEN
        st := 'excluded';
        why := CASE f.state_id WHEN 10 THEN 'postponed' WHEN 12 THEN 'cancelled'
                               WHEN 13 THEN 'kickoff to be announced' ELSE 'deleted by the provider' END
               || ': not played; apply the gameweek policy for unplayed fixtures';
    ELSIF f.state_id IN (14, 17) THEN
        st := 'complete';
        why := 'result awarded without play (walkover or awarded): no lineups, events or stats expected';
    ELSIF f.state_id = 15 THEN
        st := 'unavailable';
        why := 'abandoned: the result depends on a competition ruling';
    ELSIF coalesce(f.state_id, 1) NOT IN (5, 7, 8) THEN
        st := 'not_finished';
        why := 'not finished (state ' || coalesce(f.state_id::text, 'unknown') || ')';
    ELSE
        SELECT count(*) FILTER (WHERE type_id = 11 AND team_id = f.home_team_id),
               count(*) FILTER (WHERE type_id = 11 AND team_id = f.away_team_id),
               count(*) FILTER (WHERE sportmonks_player_id IS NULL)
          INTO hst, ast, unid
          FROM football.player_lineups
         WHERE fixture_id = p_fixture_id AND withdrawn_at IS NULL;
        -- Goal types (GET /v3/core/types): 14 goal, 15 own goal, 16 penalty.
        -- Own goals (15) are recorded on the BENEFITING team (team_id), with the
        -- scorer from the other side: verified on 16 of 16 production fixtures
        -- with own goals (24 Nov 2026). A goal the provider marks rescinded
        -- (disallowed) never counts. Every goal type needs a player id.
        SELECT count(*),
               count(*) FILTER (WHERE type_id IN (14, 15, 16) AND rescinded IS NOT TRUE AND team_id = f.home_team_id),
               count(*) FILTER (WHERE type_id IN (14, 15, 16) AND rescinded IS NOT TRUE AND team_id = f.away_team_id),
               count(*) FILTER (WHERE type_id IN (14, 15, 16) AND rescinded IS NOT TRUE AND sportmonks_player_id IS NULL)
          INTO ev, hg, ag, nosc
          FROM football.match_events
         WHERE fixture_id = p_fixture_id AND withdrawn_at IS NULL;
        SELECT count(*) INTO stats FROM football.player_match_stats WHERE fixture_id = p_fixture_id;

        inputs := jsonb_build_object(
            'result', jsonb_build_object('state_id', f.state_id, 'home', f.home_score, 'away', f.away_score),
            'starters', jsonb_build_object('home', hst, 'away', ast),
            'events', ev,
            'goal_events', jsonb_build_object('home', hg, 'away', ag),
            'match_stats', stats,
            'unidentified_lineup_players', unid);

        IF f.home_score IS NULL OR f.away_score IS NULL THEN
            issues := issues || jsonb_build_object('input', 'result', 'issue', 'score_missing', 'blocking', true,
                'detail', 'finished state without a final score');
        END IF;
        IF hst = 0 AND ast = 0 THEN
            issues := issues || jsonb_build_object('input', 'lineups', 'issue', 'missing', 'blocking', true,
                'detail', 'no starting lineup for either team');
        ELSIF hst < 11 OR ast < 11 THEN
            issues := issues || jsonb_build_object('input', 'lineups', 'issue', 'incomplete', 'blocking', true,
                'detail', format('starters: home %s, away %s (11 each expected)', hst, ast));
        END IF;
        IF ev = 0 THEN
            issues := issues || jsonb_build_object('input', 'events', 'issue', 'missing', 'blocking', true,
                'detail', 'no events at all: a played match still has substitutions or cards, so a quiet match cannot be told from a missing feed');
        ELSIF f.home_score IS NOT NULL AND (hg <> f.home_score OR ag <> f.away_score) THEN
            issues := issues || jsonb_build_object('input', 'events', 'issue', 'goals_do_not_match_score', 'blocking', true,
                'detail', format('goal events %s-%s, score %s-%s', hg, ag, f.home_score, f.away_score));
        END IF;
        IF nosc > 0 THEN
            issues := issues || jsonb_build_object('input', 'events', 'issue', 'goal_without_scorer', 'blocking', true,
                'detail', format('%s goal(s) with no scorer id', nosc));
        END IF;
        IF stats = 0 THEN
            issues := issues || jsonb_build_object('input', 'match_stats', 'issue', 'missing', 'blocking', true,
                'detail', 'no statistics rows');
        END IF;
        IF unid > 0 THEN
            issues := issues || jsonb_build_object('input', 'lineups', 'issue', 'unidentified_players', 'blocking', false,
                'detail', format('%s lineup row(s) without a player id: those players cannot be scored', unid));
        END IF;

        SELECT count(*) INTO blocking FROM jsonb_array_elements(issues) i WHERE (i ->> 'blocking')::boolean;

        IF p_at < settle_until THEN
            st := 'settling';
            why := 'finished; inside the 4 h settle window until ' || to_char(settle_until AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC';
        ELSIF blocking = 0 THEN
            st := 'complete';
            why := CASE WHEN f.home_score = 0 AND f.away_score = 0
                THEN format('verified quiet 0-0: %s starters, %s events, no goal events, %s stat rows', hst + ast, ev, stats)
                ELSE format('final %s-%s: goal events match the score, %s starters, %s events, %s stat rows',
                            f.home_score, f.away_score, hst + ast, ev, stats) END;
        ELSIF p_at >= deadline THEN
            st := 'unavailable';
            why := 'still missing 72 h after kickoff, treated as permanently unavailable: '
                   || (SELECT string_agg(i ->> 'input' || ' ' || (i ->> 'issue'), ', ')
                         FROM jsonb_array_elements(issues) i WHERE (i ->> 'blocking')::boolean);
        ELSE
            st := 'incomplete';
            why := 'waiting for: '
                   || (SELECT string_agg(i ->> 'input' || ' ' || (i ->> 'issue'), ', ')
                         FROM jsonb_array_elements(issues) i WHERE (i ->> 'blocking')::boolean)
                   || '; unavailable from ' || to_char(deadline AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') || ' UTC';
        END IF;
    END IF;

    INSERT INTO sync.fixture_completeness AS c
        (fixture_id, sportmonks_fixture_id, gameweek_id, status, reason, inputs, unresolved,
         revision, assessed_at, first_complete_at, completed_revision, rule_version)
    VALUES
        (p_fixture_id, f.sportmonks_fixture_id, sync.fixture_gameweek(p_fixture_id),
         st, why, inputs, issues, p_revision, p_at,
         CASE WHEN st = 'complete' THEN p_at END,
         CASE WHEN st = 'complete' THEN p_revision END, 2)
    ON CONFLICT (fixture_id) DO UPDATE SET
        gameweek_id        = excluded.gameweek_id,
        status             = excluded.status,
        reason             = excluded.reason,
        inputs             = excluded.inputs,
        unresolved         = excluded.unresolved,
        revision           = excluded.revision,
        assessed_at        = excluded.assessed_at,
        first_complete_at  = coalesce(c.first_complete_at, excluded.first_complete_at),
        completed_revision = coalesce(excluded.completed_revision, c.completed_revision),
        rule_version       = excluded.rule_version;
    RETURN st;
END $$;

-- 5 ---- per-gameweek rollup ---------------------------------------------------
CREATE OR REPLACE VIEW sync.gameweek_completeness AS
WITH fx AS (
    SELECT f.id AS fixture_id, sync.fixture_gameweek(f.id) AS gameweek_id,
           coalesce(c.status, 'not_assessed') AS status
    FROM football.fixtures f
    LEFT JOIN sync.fixture_completeness c ON c.fixture_id = f.id
    WHERE NOT f.is_placeholder
      AND EXISTS (SELECT 1 FROM app.gameweeks g WHERE g.season_id = f.season_id)  -- seasons with gameweeks
)
SELECT g.id AS gameweek_id, g.season_id, g.number, g.start_date, g.end_date,
       count(fx.fixture_id)                                     AS fixtures,
       count(*) FILTER (WHERE fx.status = 'complete')           AS complete,
       count(*) FILTER (WHERE fx.status = 'excluded')           AS excluded,
       count(*) FILTER (WHERE fx.status = 'settling')           AS settling,
       count(*) FILTER (WHERE fx.status = 'incomplete')         AS incomplete,
       count(*) FILTER (WHERE fx.status = 'unavailable')        AS unavailable,
       count(*) FILTER (WHERE fx.status IN ('not_finished', 'not_assessed')) AS not_finished,
       -- ready to score: something was played and every assigned fixture is complete or excluded
       (count(*) FILTER (WHERE fx.status = 'complete') > 0
        AND count(*) FILTER (WHERE fx.status NOT IN ('complete', 'excluded')) = 0) AS ready,
       -- needs an admin: a fixture is permanently unavailable (72 h rule or abandoned)
       count(*) FILTER (WHERE fx.status = 'unavailable') > 0    AS needs_admin
FROM app.gameweeks g
LEFT JOIN fx ON fx.gameweek_id = g.id
GROUP BY g.id, g.season_id, g.number, g.start_date, g.end_date;

COMMENT ON VIEW sync.gameweek_completeness IS
    'Per gameweek, from sync.fixture_completeness over the fixtures assigned to it (V043). '
    'ready = at least one fixture complete and every other one complete or excluded. '
    'Replaces sync.gameweek_sync_status, which production never populated.';
GRANT SELECT ON sync.gameweek_completeness TO app_api_user;
