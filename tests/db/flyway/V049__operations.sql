-- V049 (was V042, renumbered 24 Nov 2026 to run after V043–V048): Sprint 5. Operational visibility and a single active scheduler.
--
--   1. sync_jobs: which runner ran the job, and partial-response counters
--   2. sync.scheduler_lease: exactly one scheduler (Netlify or the Python
--      worker) runs the sync jobs; the other stands by. Takeover only after
--      the holder's heartbeat is 10 minutes old.
--   3. sync.ops_responders: who answers each alert (edited by an admin)
--   4. sync.ops_health: one row per check, with status, value, threshold,
--      responder and the runbook section to follow
--   5. sync.ops_alerts: notification state, so an alert is sent once per
--      incident (plus reminders), not every five minutes

-- 1 -------------------------------------------------------------------------
ALTER TABLE sync.sync_jobs ADD COLUMN IF NOT EXISTS runner VARCHAR(30);
ALTER TABLE sync.sync_jobs ADD COLUMN IF NOT EXISTS fixtures_missing INTEGER;
ALTER TABLE sync.sync_jobs ADD COLUMN IF NOT EXISTS stale_snapshots INTEGER;
COMMENT ON COLUMN sync.sync_jobs.runner IS 'netlify | python-worker | operator (manual reconcile script)';
COMMENT ON COLUMN sync.sync_jobs.fixtures_missing IS 'Due fixtures absent from the provider response (partial response)';
COMMENT ON COLUMN sync.sync_jobs.stale_snapshots IS 'Snapshots refused because a newer one was already committed';
CREATE INDEX IF NOT EXISTS idx_sync_jobs_started ON sync.sync_jobs (started_at);

-- 2 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.scheduler_lease (
    id            SMALLINT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    runner        VARCHAR(30) NOT NULL,
    acquired_at   TIMESTAMPTZ NOT NULL,
    heartbeat_at  TIMESTAMPTZ NOT NULL
);

-- Called at the start of every scheduled job. Granted when the caller holds
-- the lease, nobody does, or the holder's heartbeat is older than
-- p_stale_after (the holder stopped). Renews the heartbeat when granted.
CREATE OR REPLACE FUNCTION sync.claim_scheduler_lease(
    p_runner TEXT, p_stale_after INTERVAL DEFAULT interval '10 minutes')
RETURNS TABLE (granted BOOLEAN, holder TEXT, holder_heartbeat_at TIMESTAMPTZ)
LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE
    cur sync.scheduler_lease%ROWTYPE;
    t   TIMESTAMPTZ := clock_timestamp();
BEGIN
    INSERT INTO sync.scheduler_lease (id, runner, acquired_at, heartbeat_at)
    VALUES (1, p_runner, t, t)
    ON CONFLICT (id) DO NOTHING;
    SELECT * INTO cur FROM sync.scheduler_lease WHERE id = 1 FOR UPDATE;
    IF cur.runner = p_runner THEN
        UPDATE sync.scheduler_lease SET heartbeat_at = t WHERE id = 1;
        granted := true; holder := p_runner; holder_heartbeat_at := t;
    ELSIF cur.heartbeat_at < t - p_stale_after THEN
        UPDATE sync.scheduler_lease SET runner = p_runner, acquired_at = t, heartbeat_at = t WHERE id = 1;
        granted := true; holder := p_runner; holder_heartbeat_at := t;
    ELSE
        granted := false; holder := cur.runner; holder_heartbeat_at := cur.heartbeat_at;
    END IF;
    RETURN NEXT;
END $$;

-- 3 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.ops_responders (
    role        VARCHAR(30) PRIMARY KEY,
    responder   TEXT NOT NULL,
    covers      TEXT NOT NULL
);
INSERT INTO sync.ops_responders (role, responder, covers) VALUES
    ('sync-oncall',      'Phathu', 'scheduler, worker jobs, database writes, quota'),
    ('provider-account', 'Phathu', 'Sportmonks subscription, API key, coverage'),
    ('api-owner',        'Leruo',  'scoring decisions: unresolved ids, incomplete fixtures, rescoring')
ON CONFLICT (role) DO NOTHING;

-- 4 -------------------------------------------------------------------------
-- Known Sportmonks state ids (/v3/core/states, 23 Nov 2026): 1-23, 25, 26.
CREATE OR REPLACE VIEW sync.ops_health AS
WITH
live AS (
    SELECT count(*) FILTER (WHERE poll_health = 'stalled') AS stalled,
           max(observation_age) FILTER (WHERE poll_health = 'stalled') AS oldest,
           count(*) AS active
    FROM sync.live_fixture_health
),
lease AS (SELECT runner, now() - heartbeat_at AS age FROM sync.scheduler_lease WHERE id = 1),
last_ok AS (
    SELECT job_type, now() - max(completed_at) AS age
    FROM sync.sync_jobs WHERE status = 'success' GROUP BY job_type
),
quota AS (
    SELECT remaining, resets_at FROM sync.provider_quota
    WHERE entity = 'Fixture' AND resets_at > now()
),
running AS (
    SELECT count(*) FILTER (WHERE n > 1) AS duplicated,
           sum(stuck) AS stuck
    FROM (SELECT job_type, count(*) AS n,
                 count(*) FILTER (WHERE now() - started_at >
                     CASE job_type WHEN 'live_sync' THEN interval '5 minutes'
                                   WHEN 'full_sync' THEN interval '2 hours'
                                   ELSE interval '30 minutes' END) AS stuck
          FROM sync.sync_jobs WHERE status = 'running' GROUP BY job_type) r
),
runners AS (
    SELECT count(DISTINCT runner) AS n, string_agg(DISTINCT runner, ', ') AS names
    FROM sync.sync_jobs
    WHERE started_at > now() - interval '30 minutes' AND runner IS NOT NULL AND runner <> 'operator'
),
failures AS (
    SELECT count(*) AS n,
           (array_agg(job_type || ' #' || id || ': ' || left(coalesce(error_message, ''), 120)
                      ORDER BY id DESC))[1] AS latest
    FROM sync.sync_jobs
    WHERE status = 'failed' AND started_at > now() - interval '1 hour'
),
partial AS (
    SELECT coalesce(sum(fixtures_missing), 0) AS missing, coalesce(sum(stale_snapshots), 0) AS stale
    FROM sync.sync_jobs WHERE started_at > now() - interval '1 hour'
),
recent AS (
    SELECT c.* FROM sync.fixture_completeness c
    JOIN football.fixtures f ON f.id = c.fixture_id
    WHERE f.starting_at > now() - interval '14 days'
),
unresolved AS (
    SELECT count(*) FILTER (WHERE EXISTS (
               SELECT 1 FROM jsonb_array_elements(unresolved) i
               WHERE i ->> 'issue' IN ('goal_without_scorer', 'unidentified_players'))) AS ids,
           count(*) FILTER (WHERE status IN ('incomplete', 'unavailable')) AS incomplete
    FROM recent
),
unknown AS (
    SELECT count(*) AS n, string_agg(DISTINCT state_id::text, ', ') AS states
    FROM football.fixtures
    WHERE starting_at BETWEEN now() - interval '7 days' AND now() + interval '1 day'
      AND state_id IS NOT NULL AND state_id NOT IN (1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,25,26)
),
checks (check_name, status, value, threshold, role, action) AS (
    SELECT 'active_fixture_observation_age',
           CASE WHEN stalled > 0 THEN 'alert' ELSE 'ok' END,
           CASE WHEN stalled > 0 THEN stalled || ' of ' || active || ' active fixture(s) stalled; oldest observation '
                                      || to_char(oldest, 'HH24"h"MI"m"') || ' ago'
                ELSE active || ' active fixture(s), all observed within 3 min' END,
           'every active fixture observed within 3 min', 'sync-oncall', 'runbook §2 stale active fixture'
    FROM live
    UNION ALL
    SELECT 'scheduler_heartbeat',
           CASE WHEN l.runner IS NULL THEN 'warn' WHEN l.age > interval '5 minutes' THEN 'alert' ELSE 'ok' END,
           coalesce(l.runner || ' last heartbeat ' || to_char(l.age, 'HH24"h"MI"m"SS"s"') || ' ago', 'no scheduler has run yet'),
           'heartbeat within 5 min', 'sync-oncall', 'runbook §3 restart the scheduler'
    FROM (SELECT 1) one LEFT JOIN lease l ON true
    UNION ALL
    SELECT 'last_reconciliation',
           CASE WHEN f.age IS NULL OR f.age > interval '26 hours' THEN 'alert'
                WHEN w.age IS NULL OR w.age > interval '150 minutes' THEN 'warn' ELSE 'ok' END,
           'full_sync ' || coalesce(to_char(f.age, 'DD"d "HH24"h"MI"m"') || ' ago', 'never')
           || '; fixture_sync ' || coalesce(to_char(w.age, 'DD"d "HH24"h"MI"m"') || ' ago', 'never'),
           'full_sync within 26 h, fixture_sync within 150 min', 'sync-oncall', 'runbook §4 reconcile a missed window'
    FROM (SELECT 1) one
    LEFT JOIN last_ok f ON f.job_type = 'full_sync'
    LEFT JOIN last_ok w ON w.job_type = 'fixture_sync'
    UNION ALL
    SELECT 'quota_headroom',
           CASE WHEN q.remaining IS NULL THEN 'ok' WHEN q.remaining <= 120 THEN 'alert'
                WHEN q.remaining <= 300 THEN 'warn' ELSE 'ok' END,
           coalesce(q.remaining || ' Fixture calls left, resets ' || to_char(q.resets_at AT TIME ZONE 'UTC', 'HH24:MI') || ' UTC',
                    'no current quota window recorded'),
           'more than 300 calls left (broad jobs stop at 120)', 'provider-account', 'runbook §5 quota exhausted'
    FROM (SELECT 1) one LEFT JOIN quota q ON true
    UNION ALL
    SELECT 'job_overlap',
           CASE WHEN r.duplicated > 0 OR coalesce(r.stuck, 0) > 0 OR ru.n > 1 THEN 'alert' ELSE 'ok' END,
           coalesce(r.duplicated, 0) || ' job type(s) running twice; ' || coalesce(r.stuck, 0) || ' stuck job(s); '
           || 'schedulers active in 30 min: ' || coalesce(ru.names, 'none'),
           'one running job per type, none stuck, one scheduler', 'sync-oncall', 'runbook §6 overlapping or stuck jobs'
    FROM (SELECT 1) one LEFT JOIN running r ON true LEFT JOIN runners ru ON true
    UNION ALL
    SELECT 'failed_commits',
           CASE WHEN n >= 3 THEN 'alert' WHEN n > 0 THEN 'warn' ELSE 'ok' END,
           n || ' failed job(s) in the last hour' || coalesce('; latest ' || latest, ''),
           'no failed jobs', 'sync-oncall', 'runbook §7 failing jobs'
    FROM failures
    UNION ALL
    SELECT 'partial_responses',
           CASE WHEN missing > 0 THEN 'warn' ELSE 'ok' END,
           missing || ' due fixture(s) missing from provider responses, ' || stale || ' stale snapshot(s) refused, last hour',
           'no due fixture missing from a response', 'provider-account', 'runbook §8 partial responses'
    FROM partial
    UNION ALL
    SELECT 'unresolved_ids',
           CASE WHEN ids > 0 THEN 'warn' ELSE 'ok' END,
           ids || ' fixture(s) in the last 14 days with a goal or lineup player missing its id',
           'none', 'api-owner', 'runbook §9 unresolved ids and incomplete fixtures'
    FROM unresolved
    UNION ALL
    SELECT 'incomplete_fixtures',
           CASE WHEN incomplete > 0 THEN 'warn' ELSE 'ok' END,
           incomplete || ' fixture(s) in the last 14 days incomplete or unavailable after settling',
           'none', 'api-owner', 'runbook §9 unresolved ids and incomplete fixtures'
    FROM unresolved
    UNION ALL
    SELECT 'unknown_provider_states',
           CASE WHEN n > 0 THEN 'warn' ELSE 'ok' END,
           n || ' recent fixture(s) in a state id we do not know' || coalesce(' (' || states || ')', ''),
           'all state ids known', 'sync-oncall', 'runbook §10 unknown provider state'
    FROM unknown
)
SELECT c.check_name, c.status, c.value, c.threshold, c.role AS responder_role,
       r.responder, c.action, now() AS checked_at
FROM checks c LEFT JOIN sync.ops_responders r ON r.role = c.role;

COMMENT ON VIEW sync.ops_health IS
    'One row per operational check: status ok/warn/alert, current value, threshold, '
    'who responds and which runbook section to follow (docs/runbook.md).';

-- 5 -------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sync.ops_alerts (
    check_name     VARCHAR(40) PRIMARY KEY,
    status         VARCHAR(10) NOT NULL,
    first_seen_at  TIMESTAMPTZ NOT NULL,
    last_sent_at   TIMESTAMPTZ
);

-- Grants -----------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON sync.scheduler_lease, sync.ops_alerts TO sync_worker_user;
GRANT SELECT ON sync.ops_responders, sync.ops_health TO sync_worker_user;
GRANT SELECT ON sync.scheduler_lease, sync.ops_responders, sync.ops_health, sync.ops_alerts TO app_api_user;
REVOKE INSERT, UPDATE ON sync.ops_responders FROM sync_worker_user;
REVOKE ALL ON FUNCTION sync.claim_scheduler_lease(TEXT, INTERVAL) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION sync.claim_scheduler_lease(TEXT, INTERVAL) TO sync_worker_user;
