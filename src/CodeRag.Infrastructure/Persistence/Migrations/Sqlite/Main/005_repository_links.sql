-- CodeRag — SQLite MAIN database: manual repository links (005)
-- Human-declared (and later auto-suggested) directed relationships between repositories.
-- Enables cross-repository relation resolution: symbols in the source may reference symbols
-- in the target (e.g. an app repo calling SQL objects in a DB repo).
CREATE TABLE IF NOT EXISTS repository_links (
    id TEXT PRIMARY KEY,
    source_repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    target_repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    link_type TEXT NOT NULL,
    confidence REAL,
    note TEXT,
    created_at TEXT NOT NULL,
    UNIQUE(source_repository_id, target_repository_id, link_type)
);
CREATE INDEX IF NOT EXISTS idx_repository_links_source ON repository_links(source_repository_id);
CREATE INDEX IF NOT EXISTS idx_repository_links_target ON repository_links(target_repository_id);
