-- CodeRag — SQLite MAIN database: per-object usage statistics (006)
-- Counts how often a symbol is used/queried (CodeAtlas lookups, cross-repo refs, ...).
CREATE TABLE IF NOT EXISTS symbol_usage (
    symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    repository_id TEXT NOT NULL,
    usage_type TEXT NOT NULL,
    source TEXT NOT NULL,
    count INTEGER NOT NULL DEFAULT 0,
    last_used_at TEXT NOT NULL,
    PRIMARY KEY(symbol_id, usage_type, source)
);
CREATE INDEX IF NOT EXISTS idx_symbol_usage_repo ON symbol_usage(repository_id, count DESC);
CREATE INDEX IF NOT EXISTS idx_symbol_usage_symbol ON symbol_usage(symbol_id);
