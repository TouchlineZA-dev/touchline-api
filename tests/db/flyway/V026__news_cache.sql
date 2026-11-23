-- V026: News cache table
-- Stores GNews API responses to reduce daily quota consumption.
-- Each row represents one unique query result, keyed by cache_key.
-- Rows with expires_at < NOW() are considered stale and will be refreshed.

CREATE TABLE IF NOT EXISTS app.news_cache (
    id          SERIAL       PRIMARY KEY,
    cache_key   VARCHAR(255) NOT NULL UNIQUE,  -- e.g. "headlines:10", "team:orlando_pirates:10"
    response    JSONB        NOT NULL,          -- full GNewsResponse JSON blob
    fetched_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    expires_at  TIMESTAMPTZ  NOT NULL           -- set to NOW() + cache TTL on insert/update
);

CREATE INDEX IF NOT EXISTS idx_news_cache_key     ON app.news_cache(cache_key);
CREATE INDEX IF NOT EXISTS idx_news_cache_expires ON app.news_cache(expires_at);
