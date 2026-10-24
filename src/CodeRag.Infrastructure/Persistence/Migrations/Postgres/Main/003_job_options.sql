-- CodeRag — PostgreSQL MAIN database: per-job JSON options (003)
-- Generic nullable options bag consumed by job handlers.
-- Example (documentation): {"force":false,"maxSymbols":25}
ALTER TABLE indexing_jobs ADD COLUMN IF NOT EXISTS options TEXT;
