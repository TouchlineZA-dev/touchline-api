-- sync.sync_logs is append-only: no updated_at column, no update trigger.
CREATE TABLE sync.sync_logs (
    id              SERIAL PRIMARY KEY,
    sync_job_id     INTEGER NOT NULL REFERENCES sync.sync_jobs(id),
    message         TEXT NOT NULL,
    level           VARCHAR(10) NOT NULL DEFAULT 'info',
    logged_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
