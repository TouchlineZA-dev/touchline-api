-- V040: Sprint 3. Live freshness, committed revisions, quota, withdrawn events.
--
-- 1. football.match_events.withdrawn_at: the provider stopped listing an event
--    we stored (VAR reversal, an event deleted and reissued under a new id).
--    The worker marks it instead of deleting it, so it needs no DELETE grant
--    and the history stays auditable. Readers filter `withdrawn_at IS NULL`.
-- 2. sync.fixture_freshness: one row per fixture. When we last observed it,
--    when its committed content last changed, and its current revision.
-- 3. sync.fixture_revisions: append-only. One row each time a fixture's
--    committed content changes, with provider / observation / commit times.
-- 4. sync.record_fixture_observation(): called by BOTH writers (Python and
--    Netlify) as the last statement of each fixture's transaction, so the
--    revision logic exists once, in the database.
-- 5. sync.provider_quota and sync_jobs usage columns: Sportmonks calls spent
--    and the remaining hourly allowance, per entity.
-- 6. Read views for the API: sync.live_fixture_health, sync.fixture_revision_latency.

-- 1 -------------------------------------------------------------------------
ALTER TABLE football.match_events ADD COLUMN IF NOT EXISTS withdrawn_at TIMESTAMPTZ;
COMMENT ON COLUMN football.match_events.withdrawn_at IS
    'Set when a full fixture snapshot no longer lists this event (VAR reversal, '
    'reissued correction). NULL = current. Cleared if the provider lists it again.';

-- 2 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.fixture_freshness (
    fixture_id              INTEGER PRIMARY KEY REFERENCES football.fixtures (id),
    sportmonks_fixture_id   BIGINT NOT NULL,
    revision                INTEGER NOT NULL,
    content_hash            TEXT NOT NULL,
    last_observed_at        TIMESTAMPTZ NOT NULL,
    last_committed_at       TIMESTAMPTZ NOT NULL,
    last_changed_at         TIMESTAMPTZ NOT NULL,
    last_provider_at        TIMESTAMPTZ,
    sync_job_id             INTEGER REFERENCES sync.sync_jobs (id)
);

-- 3 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.fixture_revisions (
    id                      BIGSERIAL PRIMARY KEY,
    fixture_id              INTEGER NOT NULL REFERENCES football.fixtures (id),
    sportmonks_fixture_id   BIGINT NOT NULL,
    revision                INTEGER NOT NULL,
    content_hash            TEXT NOT NULL,
    state_id                SMALLINT,
    home_score              SMALLINT,
    away_score              SMALLINT,
    event_count             INTEGER NOT NULL,
    provider_at             TIMESTAMPTZ,
    observed_at             TIMESTAMPTZ NOT NULL,
    committed_at            TIMESTAMPTZ NOT NULL,
    sync_job_id             INTEGER REFERENCES sync.sync_jobs (id),
    UNIQUE (fixture_id, revision)
);
CREATE INDEX IF NOT EXISTS idx_fixture_revisions_committed ON sync.fixture_revisions (committed_at);

-- 4 -------------------------------------------------------------------------
-- Hash of everything a reader can see for one fixture. Epoch seconds and
-- explicit '-' for NULL keep it independent of session time zone and of
-- which writer computed it.
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
              FROM football.player_lineups WHERE fixture_id = p_fixture_id), '')
    || '#' || coalesce((SELECT string_agg(concat_ws(',',
                sportmonks_stat_id, type_id, coalesce(value::text, '-')),
                ';' ORDER BY sportmonks_stat_id)
              FROM football.player_match_stats WHERE fixture_id = p_fixture_id), '')
)
$$;

-- Call as the LAST statement of a fixture's write transaction. Bumps the
-- revision only when the committed content changed; always records the
-- observation. committed_at is clock_timestamp() at this point, i.e. just
-- before COMMIT.
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
    h    TEXT := sync.fixture_content_hash(p_fixture_id);
    t    TIMESTAMPTZ := clock_timestamp();
    cur  sync.fixture_freshness%ROWTYPE;
    f    football.fixtures%ROWTYPE;
    rev  INTEGER;
BEGIN
    SELECT * INTO cur FROM sync.fixture_freshness WHERE fixture_id = p_fixture_id FOR UPDATE;
    IF FOUND AND cur.content_hash = h THEN
        UPDATE sync.fixture_freshness
           SET last_observed_at  = greatest(last_observed_at, p_observed_at),
               last_committed_at = t,
               last_provider_at  = coalesce(p_provider_at, last_provider_at),
               sync_job_id       = p_sync_job_id
         WHERE fixture_id = p_fixture_id;
        out_revision := cur.revision; out_changed := false;
        RETURN NEXT; RETURN;
    END IF;

    SELECT * INTO f FROM football.fixtures WHERE id = p_fixture_id;
    rev := coalesce(cur.revision, 0) + 1;

    INSERT INTO sync.fixture_revisions
        (fixture_id, sportmonks_fixture_id, revision, content_hash, state_id,
         home_score, away_score, event_count, provider_at, observed_at, committed_at, sync_job_id)
    VALUES
        (p_fixture_id, f.sportmonks_fixture_id, rev, h, f.state_id,
         f.home_score, f.away_score,
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

    out_revision := rev; out_changed := true;
    RETURN NEXT;
END $$;

-- 5 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.provider_quota (
    entity       VARCHAR(50) PRIMARY KEY,
    remaining    INTEGER NOT NULL,
    resets_at    TIMESTAMPTZ NOT NULL,
    observed_at  TIMESTAMPTZ NOT NULL
);

ALTER TABLE sync.sync_jobs ADD COLUMN IF NOT EXISTS api_calls INTEGER;
ALTER TABLE sync.sync_jobs ADD COLUMN IF NOT EXISTS quota_remaining INTEGER;

-- 6 -------------------------------------------------------------------------
-- Live states per Sportmonks /v3/core/states (fetched 23 Nov 2026):
-- in play 2,6,9,22,23; breaks 3,4,21,25; interrupted 11,16,18,19,26.
CREATE OR REPLACE VIEW sync.live_fixture_health AS
SELECT f.id                     AS fixture_id,
       f.sportmonks_fixture_id,
       f.state_id,
       f.home_score,
       f.away_score,
       f.starting_at,
       fr.revision,
       fr.last_observed_at,
       fr.last_changed_at,
       now() - fr.last_observed_at AS observation_age,
       CASE
           WHEN fr.last_observed_at IS NULL
             OR now() - fr.last_observed_at > interval '3 minutes' THEN 'stalled'
           WHEN now() - fr.last_changed_at > interval '15 minutes' THEN 'healthy_quiet'
           ELSE 'healthy'
       END AS poll_health
FROM football.fixtures f
LEFT JOIN sync.fixture_freshness fr ON fr.fixture_id = f.id
WHERE f.state_id IN (2, 3, 4, 6, 9, 11, 16, 18, 19, 21, 22, 23, 25, 26)
   OR (coalesce(f.state_id, 1) = 1
       AND f.starting_at BETWEEN now() - interval '3 hours' AND now());

COMMENT ON VIEW sync.live_fixture_health IS
    'Fixtures that should be live now. stalled = not observed for 3 min (poller '
    'problem); healthy_quiet = observed recently, nothing changed for 15 min '
    '(e.g. a scoreless spell); healthy = observed and changing.';

CREATE OR REPLACE VIEW sync.fixture_revision_latency AS
SELECT r.*,
       lag(r.state_id) OVER w                  AS previous_state_id,
       r.committed_at - r.observed_at          AS observe_to_commit,
       CASE WHEN r.state_id IS DISTINCT FROM lag(r.state_id) OVER w
            THEN r.committed_at - r.provider_at END AS provider_to_commit
FROM sync.fixture_revisions r
WINDOW w AS (PARTITION BY r.fixture_id ORDER BY r.revision);

COMMENT ON VIEW sync.fixture_revision_latency IS
    'provider_to_commit is only set on revisions where the phase changed: the '
    'provider time comes from the period start/end timestamps. Events carry '
    'no provider time, so for them only observe_to_commit is available.';

-- Grants (explicit, alongside the V028 default privileges) ------------------
GRANT SELECT, INSERT, UPDATE ON sync.fixture_freshness, sync.fixture_revisions,
                                sync.provider_quota TO sync_worker_user;
GRANT USAGE, SELECT ON SEQUENCE sync.fixture_revisions_id_seq TO sync_worker_user;
GRANT SELECT ON sync.fixture_freshness, sync.fixture_revisions, sync.provider_quota,
                sync.live_fixture_health, sync.fixture_revision_latency TO app_api_user;
GRANT SELECT ON sync.live_fixture_health, sync.fixture_revision_latency TO sync_worker_user;

REVOKE ALL ON FUNCTION sync.fixture_content_hash(INTEGER) FROM PUBLIC;
REVOKE ALL ON FUNCTION sync.record_fixture_observation(INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.fixture_content_hash(INTEGER) TO sync_worker_user, app_api_user;
GRANT EXECUTE ON FUNCTION sync.record_fixture_observation(INTEGER, TIMESTAMPTZ, TIMESTAMPTZ, INTEGER) TO sync_worker_user;
