-- V029: add job_type to sync.sync_jobs.
--
-- Full syncs and fixture syncs were indistinguishable in the audit trail, and
-- the "is a job of this type already running?" guard (H4) needs to tell them
-- apart. Safe NOT NULL add on a populated table: add nullable, backfill, constrain.

ALTER TABLE sync.sync_jobs ADD COLUMN job_type VARCHAR(30);

UPDATE sync.sync_jobs SET job_type = 'unknown' WHERE job_type IS NULL;

ALTER TABLE sync.sync_jobs ALTER COLUMN job_type SET NOT NULL;

-- Backs the concurrency guard get_running_job(job_type) and status filtering.
CREATE INDEX IF NOT EXISTS idx_sync_jobs_type_status
    ON sync.sync_jobs (job_type, status);
