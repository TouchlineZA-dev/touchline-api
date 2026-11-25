-- V041: Sprint 4. Make late changes and incomplete matches explainable.
--
-- Everything here runs inside the database so the Python worker and the
-- Netlify writer behave identically:
--   1. event identity + history: match_events.attribution_revision and
--      sync.event_changes (one row per observed/corrected/withdrawn/reinstated
--      event), written by triggers on football.match_events
--   2. lineup withdrawal column (a COMPLETE XI that drops a player)
--   3. reschedules: sync.fixture_schedule_changes, old and new kickoff, state
--      and gameweek, written by a trigger on football.fixtures
--   4. out-of-order guard: sync.begin_fixture_observation() refuses a snapshot
--      older than the newest one already committed for that fixture
--   5. completeness: sync.fixture_completeness, assessed by
--      sync.assess_fixture() after every observation, with inputs, unresolved
--      facts, assessment time and reason
--   6. late evidence: sync.late_corrections, one row whenever a fixture that
--      was assessed complete commits a new revision
--
-- Live state ids per Sportmonks /v3/core/states (see V040).

-- 1 ---- event identity and history ----------------------------------------
ALTER TABLE football.match_events
    ADD COLUMN IF NOT EXISTS attribution_revision INTEGER NOT NULL DEFAULT 1;
COMMENT ON COLUMN football.match_events.attribution_revision IS
    'Starts at 1; +1 each time type, minute, scorer, assist, result, rescinded or '
    'withdrawal changes. The event keeps its id; history is in sync.event_changes.';

CREATE TABLE IF NOT EXISTS sync.event_changes (
    id                    BIGSERIAL PRIMARY KEY,
    event_id              INTEGER NOT NULL,
    sportmonks_event_id   BIGINT NOT NULL,
    fixture_id            INTEGER NOT NULL,
    change                VARCHAR(12) NOT NULL
                          CHECK (change IN ('observed', 'corrected', 'withdrawn', 'reinstated')),
    fields                TEXT[] NOT NULL DEFAULT '{}',
    old                   JSONB,
    new                   JSONB NOT NULL,
    attribution_revision  INTEGER NOT NULL,
    fixture_revision      INTEGER,            -- set by record_fixture_observation in the same transaction
    changed_at            TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_event_changes_fixture ON sync.event_changes (fixture_id, fixture_revision);

CREATE OR REPLACE FUNCTION sync.event_attribution(e football.match_events)
RETURNS JSONB LANGUAGE sql IMMUTABLE AS $$
    SELECT jsonb_build_object(
        'type_id', e.type_id, 'minute', e.minute, 'extra_minute', e.extra_minute,
        'sportmonks_player_id', e.sportmonks_player_id,
        'sportmonks_related_player_id', e.sportmonks_related_player_id,
        'result', e.result, 'rescinded', e.rescinded,
        'withdrawn', e.withdrawn_at IS NOT NULL)
$$;

CREATE OR REPLACE FUNCTION sync.bump_event_attribution_revision()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    IF sync.event_attribution(NEW) IS DISTINCT FROM sync.event_attribution(OLD) THEN
        NEW.attribution_revision := OLD.attribution_revision + 1;
    END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION sync.log_event_change()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
DECLARE
    n JSONB := sync.event_attribution(NEW);
    o JSONB;
    kind TEXT := 'observed';
    changed TEXT[] := '{}';
BEGIN
    IF TG_OP = 'UPDATE' THEN
        o := sync.event_attribution(OLD);
        IF o = n THEN
            RETURN NULL;   -- an idempotent re-upsert: nothing to record
        END IF;
        SELECT coalesce(array_agg(k ORDER BY k), '{}') INTO changed
          FROM jsonb_object_keys(n) k WHERE n -> k IS DISTINCT FROM o -> k;
        kind := CASE
            WHEN NOT (o ->> 'withdrawn')::boolean AND (n ->> 'withdrawn')::boolean THEN 'withdrawn'
            WHEN (o ->> 'withdrawn')::boolean AND NOT (n ->> 'withdrawn')::boolean THEN 'reinstated'
            ELSE 'corrected' END;
    END IF;
    INSERT INTO sync.event_changes
        (event_id, sportmonks_event_id, fixture_id, change, fields, old, new,
         attribution_revision, changed_at)
    VALUES
        (NEW.id, NEW.sportmonks_event_id, NEW.fixture_id, kind, changed, o, n,
         NEW.attribution_revision, clock_timestamp());
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS trg_match_events_attribution_revision ON football.match_events;
CREATE TRIGGER trg_match_events_attribution_revision
    BEFORE UPDATE ON football.match_events
    FOR EACH ROW EXECUTE FUNCTION sync.bump_event_attribution_revision();

DROP TRIGGER IF EXISTS trg_match_events_log_change ON football.match_events;
CREATE TRIGGER trg_match_events_log_change
    AFTER INSERT OR UPDATE ON football.match_events
    FOR EACH ROW EXECUTE FUNCTION sync.log_event_change();

-- 2 ---- lineups ------------------------------------------------------------
ALTER TABLE football.player_lineups ADD COLUMN IF NOT EXISTS withdrawn_at TIMESTAMPTZ;
COMMENT ON COLUMN football.player_lineups.withdrawn_at IS
    'Set only when a snapshot carries a COMPLETE starting XI (11 starters) for '
    'this team and no longer lists this row, e.g. a late team-sheet change. A '
    'partial or missing lineup never withdraws anything.';

-- Same formula as V040; withdrawn lineup rows are now left out, which changes
-- no existing hash because no row was withdrawn before this migration.
CREATE OR REPLACE FUNCTION sync.fixture_content_hash(p_fixture_id INTEGER)
RETURNS TEXT LANGUAGE sql STABLE AS $$
SELECT md5(
    coalesce((SELECT concat_ws('|',
                coalesce(state_id::text, '-'),
                coalesce(home_score::text, '-'), coalesce(away_score::text, '-'),
                coalesce(home_penalty_score::text, '-'), coalesce(away_penalty_score::text, '-'),
                coalesce(extract(epoch FROM starting_at)::bigint::text, '-'))
              FROM football.fixtures WHERE id = p_fixture_id), '')
    || '#' || coalesce((SELECT string_agg(concat_ws(',',
                sportmonks_event_id, type_id,
                coalesce(minute::text, '-'), coalesce(extra_minute::text, '-'),
                coalesce(sportmonks_player_id::text, '-'),
                coalesce(sportmonks_related_player_id::text, '-'),
                coalesce(result, '-'), coalesce(rescinded::text, '-')),
                ';' ORDER BY sportmonks_event_id)
              FROM football.match_events
              WHERE fixture_id = p_fixture_id AND withdrawn_at IS NULL), '')
    || '#' || coalesce((SELECT string_agg(concat_ws(',',
                sportmonks_lineup_id, coalesce(sportmonks_player_id::text, '-'), type_id),
                ';' ORDER BY sportmonks_lineup_id)
              FROM football.player_lineups
              WHERE fixture_id = p_fixture_id AND withdrawn_at IS NULL), '')
    || '#' || coalesce((SELECT string_agg(concat_ws(',',
                sportmonks_stat_id, type_id, coalesce(value::text, '-')),
                ';' ORDER BY sportmonks_stat_id)
              FROM football.player_match_stats WHERE fixture_id = p_fixture_id), '')
)
$$;

-- 3 ---- reschedules ---------------------------------------------------------
-- Gameweek of a kickoff: the app.gameweeks row of that season whose SAST
-- calendar dates contain it (same rule as gameweek_status_service.py).
CREATE OR REPLACE FUNCTION sync.gameweek_for(p_season_id INTEGER, p_at TIMESTAMPTZ)
RETURNS INTEGER LANGUAGE sql STABLE AS $$
    SELECT id FROM app.gameweeks
    WHERE season_id = p_season_id
      AND (p_at AT TIME ZONE 'Africa/Johannesburg')::date BETWEEN start_date AND end_date
    ORDER BY start_date
    LIMIT 1
$$;

CREATE TABLE IF NOT EXISTS sync.fixture_schedule_changes (
    id                     BIGSERIAL PRIMARY KEY,
    fixture_id             INTEGER NOT NULL REFERENCES football.fixtures (id),
    sportmonks_fixture_id  BIGINT NOT NULL,
    old_starting_at        TIMESTAMPTZ,
    new_starting_at        TIMESTAMPTZ,
    old_state_id           SMALLINT,
    new_state_id           SMALLINT,
    old_gameweek_id        INTEGER,
    new_gameweek_id        INTEGER,
    fixture_revision       INTEGER,
    changed_at             TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_fixture_schedule_changes_fixture ON sync.fixture_schedule_changes (fixture_id);

-- Schedule-relevant states: postponed, cancelled, TBA, walkover, abandoned,
-- delayed, awarded, deleted. Ordinary live progress (1 -> 2 -> 3 ...) is not
-- a schedule change and is already in sync.fixture_revisions.
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
             sync.gameweek_for(OLD.season_id, OLD.starting_at),
             sync.gameweek_for(NEW.season_id, NEW.starting_at),
             clock_timestamp());
    END IF;
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS trg_fixtures_schedule_change ON football.fixtures;
CREATE TRIGGER trg_fixtures_schedule_change
    AFTER UPDATE ON football.fixtures
    FOR EACH ROW EXECUTE FUNCTION sync.log_fixture_schedule_change();

-- 4 ---- out-of-order guard --------------------------------------------------
CREATE UNIQUE INDEX IF NOT EXISTS uq_fixture_freshness_sportmonks
    ON sync.fixture_freshness (sportmonks_fixture_id);

-- First statement of a fixture's write transaction. Locks the fixture's
-- freshness row and returns false if a newer snapshot is already committed
-- (a slow retry, or two writers racing), so the caller must not write.
CREATE OR REPLACE FUNCTION sync.begin_fixture_observation(
    p_sportmonks_fixture_id BIGINT, p_observed_at TIMESTAMPTZ)
RETURNS BOOLEAN LANGUAGE plpgsql AS $$
DECLARE
    newest TIMESTAMPTZ;
BEGIN
    SELECT last_observed_at INTO newest FROM sync.fixture_freshness
     WHERE sportmonks_fixture_id = p_sportmonks_fixture_id FOR UPDATE;
    RETURN newest IS NULL OR p_observed_at >= newest;
END $$;

-- 5 ---- completeness --------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.fixture_completeness (
    fixture_id             INTEGER PRIMARY KEY REFERENCES football.fixtures (id),
    sportmonks_fixture_id  BIGINT NOT NULL,
    gameweek_id            INTEGER,
    status                 VARCHAR(14) NOT NULL CHECK (status IN
                           ('not_finished', 'settling', 'complete', 'incomplete',
                            'unavailable', 'excluded')),
    reason                 TEXT NOT NULL,
    inputs                 JSONB NOT NULL,
    unresolved             JSONB NOT NULL,
    revision               INTEGER,
    assessed_at            TIMESTAMPTZ NOT NULL,
    first_complete_at      TIMESTAMPTZ,
    completed_revision     INTEGER,
    rule_version           SMALLINT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_fixture_completeness_gameweek ON sync.fixture_completeness (gameweek_id, status);

COMMENT ON TABLE sync.fixture_completeness IS
    'One row per fixture: is it ready to score, and if not, exactly what is '
    'missing. Re-assessed on every observation. completed_revision = the latest '
    'revision assessed complete (what the API may have scored).';

-- Rule version 1. Settle window 4 h after kickoff; an input still missing
-- 72 h after kickoff is treated as permanently unavailable.
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
        -- Own goals (16) are recorded on the BENEFITING team: verified on
        -- 29 of 29 production fixtures with own goals (23 Nov 2026).
        SELECT count(*),
               count(*) FILTER (WHERE type_id IN (14, 15, 16) AND team_id = f.home_team_id),
               count(*) FILTER (WHERE type_id IN (14, 15, 16) AND team_id = f.away_team_id),
               count(*) FILTER (WHERE type_id IN (14, 15) AND sportmonks_player_id IS NULL)
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
        (p_fixture_id, f.sportmonks_fixture_id, sync.gameweek_for(f.season_id, f.starting_at),
         st, why, inputs, issues, p_revision, p_at,
         CASE WHEN st = 'complete' THEN p_at END,
         CASE WHEN st = 'complete' THEN p_revision END, 1)
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

-- 6 ---- late evidence -------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.late_corrections (
    id                     BIGSERIAL PRIMARY KEY,
    fixture_id             INTEGER NOT NULL REFERENCES football.fixtures (id),
    sportmonks_fixture_id  BIGINT NOT NULL,
    gameweek_id            INTEGER,
    completed_revision     INTEGER NOT NULL,   -- latest revision assessed complete (what may have been scored)
    previous_revision      INTEGER NOT NULL,
    revision               INTEGER NOT NULL,   -- the corrected revision now committed
    observed_at            TIMESTAMPTZ NOT NULL,
    detected_at            TIMESTAMPTZ NOT NULL,
    changes                JSONB NOT NULL,
    UNIQUE (fixture_id, revision)
);
COMMENT ON TABLE sync.late_corrections IS
    'Append-only feed: a fixture that had been assessed complete committed a new '
    'revision. Read by id > last processed id; compare completed_revision with '
    'the revision you scored, and rescore per the agreed policy.';

-- Replaces V040's version: same contract, plus links change rows to the
-- revision, records late corrections, and re-assesses completeness.
CREATE OR REPLACE FUNCTION sync.record_fixture_observation(
    p_fixture_id   INTEGER,
    p_observed_at  TIMESTAMPTZ,
    p_provider_at  TIMESTAMPTZ,
    p_sync_job_id  INTEGER
)
RETURNS TABLE (out_revision INTEGER, out_changed BOOLEAN)
LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE
    h     TEXT := sync.fixture_content_hash(p_fixture_id);
    t     TIMESTAMPTZ := clock_timestamp();
    cur   sync.fixture_freshness%ROWTYPE;
    f     football.fixtures%ROWTYPE;
    prev  sync.fixture_revisions%ROWTYPE;
    comp  sync.fixture_completeness%ROWTYPE;
    rev   INTEGER;
    chg   BOOLEAN;
BEGIN
    SELECT * INTO cur FROM sync.fixture_freshness WHERE fixture_id = p_fixture_id FOR UPDATE;
    IF FOUND AND cur.content_hash = h THEN
        UPDATE sync.fixture_freshness
           SET last_observed_at  = greatest(last_observed_at, p_observed_at),
               last_committed_at = t,
               last_provider_at  = coalesce(p_provider_at, last_provider_at),
               sync_job_id       = p_sync_job_id
         WHERE fixture_id = p_fixture_id;
        rev := cur.revision; chg := false;
    ELSE
        SELECT * INTO f FROM football.fixtures WHERE id = p_fixture_id;
        rev := coalesce(cur.revision, 0) + 1; chg := true;

        INSERT INTO sync.fixture_revisions
            (fixture_id, sportmonks_fixture_id, revision, content_hash, state_id,
             home_score, away_score, event_count, provider_at, observed_at, committed_at, sync_job_id)
        VALUES
            (p_fixture_id, f.sportmonks_fixture_id, rev, h, f.state_id, f.home_score, f.away_score,
             (SELECT count(*) FROM football.match_events
               WHERE fixture_id = p_fixture_id AND withdrawn_at IS NULL),
             p_provider_at, p_observed_at, t, p_sync_job_id);

        INSERT INTO sync.fixture_freshness
            (fixture_id, sportmonks_fixture_id, revision, content_hash, last_observed_at,
             last_committed_at, last_changed_at, last_provider_at, sync_job_id)
        VALUES
            (p_fixture_id, f.sportmonks_fixture_id, rev, h, p_observed_at, t, t, p_provider_at, p_sync_job_id)
        ON CONFLICT (fixture_id) DO UPDATE SET
            revision          = excluded.revision,
            content_hash      = excluded.content_hash,
            last_observed_at  = greatest(sync.fixture_freshness.last_observed_at, excluded.last_observed_at),
            last_committed_at = excluded.last_committed_at,
            last_changed_at   = excluded.last_changed_at,
            last_provider_at  = coalesce(excluded.last_provider_at, sync.fixture_freshness.last_provider_at),
            sync_job_id       = excluded.sync_job_id;

        -- Late evidence: the fixture had been assessed complete, and its
        -- committed content just changed.
        SELECT * INTO comp FROM sync.fixture_completeness WHERE fixture_id = p_fixture_id;
        IF FOUND AND comp.completed_revision IS NOT NULL THEN
            SELECT * INTO prev FROM sync.fixture_revisions
             WHERE fixture_id = p_fixture_id AND revision = cur.revision;
            INSERT INTO sync.late_corrections
                (fixture_id, sportmonks_fixture_id, gameweek_id, completed_revision,
                 previous_revision, revision, observed_at, detected_at, changes)
            VALUES
                (p_fixture_id, f.sportmonks_fixture_id, comp.gameweek_id, comp.completed_revision,
                 cur.revision, rev, p_observed_at, t,
                 jsonb_build_object(
                    'fixture', jsonb_strip_nulls(jsonb_build_object(
                        'state_id', CASE WHEN prev.state_id IS DISTINCT FROM f.state_id
                                         THEN jsonb_build_array(prev.state_id, f.state_id) END,
                        'score', CASE WHEN (prev.home_score, prev.away_score) IS DISTINCT FROM (f.home_score, f.away_score)
                                      THEN jsonb_build_array(prev.home_score::text || '-' || prev.away_score::text,
                                                             f.home_score::text || '-' || f.away_score::text) END)),
                    'events', coalesce((SELECT jsonb_agg(jsonb_build_object(
                                    'sportmonks_event_id', sportmonks_event_id, 'change', change,
                                    'fields', fields, 'old', old, 'new', new,
                                    'attribution_revision', attribution_revision) ORDER BY id)
                                FROM sync.event_changes
                               WHERE fixture_id = p_fixture_id AND fixture_revision IS NULL), '[]'),
                    'schedule', coalesce((SELECT jsonb_agg(jsonb_build_object(
                                    'old_starting_at', old_starting_at, 'new_starting_at', new_starting_at,
                                    'old_state_id', old_state_id, 'new_state_id', new_state_id,
                                    'old_gameweek_id', old_gameweek_id, 'new_gameweek_id', new_gameweek_id) ORDER BY id)
                                FROM sync.fixture_schedule_changes
                               WHERE fixture_id = p_fixture_id AND fixture_revision IS NULL), '[]')));
        END IF;
    END IF;

    UPDATE sync.event_changes SET fixture_revision = rev
     WHERE fixture_id = p_fixture_id AND fixture_revision IS NULL;
    UPDATE sync.fixture_schedule_changes SET fixture_revision = rev
     WHERE fixture_id = p_fixture_id AND fixture_revision IS NULL;

    PERFORM sync.assess_fixture(p_fixture_id, p_observed_at, rev);

    out_revision := rev; out_changed := chg;
    RETURN NEXT;
END $$;

-- Grants -----------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON sync.event_changes, sync.fixture_schedule_changes,
                                sync.fixture_completeness, sync.late_corrections TO sync_worker_user;
GRANT USAGE, SELECT ON SEQUENCE sync.event_changes_id_seq, sync.fixture_schedule_changes_id_seq,
                                sync.late_corrections_id_seq TO sync_worker_user;
GRANT SELECT ON sync.event_changes, sync.fixture_schedule_changes,
                sync.fixture_completeness, sync.late_corrections TO app_api_user;

REVOKE ALL ON FUNCTION sync.begin_fixture_observation(BIGINT, TIMESTAMPTZ) FROM PUBLIC;
REVOKE ALL ON FUNCTION sync.assess_fixture(INTEGER, TIMESTAMPTZ, INTEGER) FROM PUBLIC;
REVOKE ALL ON FUNCTION sync.gameweek_for(INTEGER, TIMESTAMPTZ) FROM PUBLIC;
REVOKE ALL ON FUNCTION sync.event_attribution(football.match_events) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.begin_fixture_observation(BIGINT, TIMESTAMPTZ) TO sync_worker_user;
GRANT EXECUTE ON FUNCTION sync.assess_fixture(INTEGER, TIMESTAMPTZ, INTEGER) TO sync_worker_user;
GRANT EXECUTE ON FUNCTION sync.gameweek_for(INTEGER, TIMESTAMPTZ) TO sync_worker_user, app_api_user;
GRANT EXECUTE ON FUNCTION sync.event_attribution(football.match_events) TO sync_worker_user, app_api_user;
