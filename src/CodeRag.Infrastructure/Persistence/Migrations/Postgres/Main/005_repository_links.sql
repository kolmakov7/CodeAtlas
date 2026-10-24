-- CodeRag — PostgreSQL MAIN database: manual repository links (005)
CREATE TABLE IF NOT EXISTS repository_links (
    id TEXT PRIMARY KEY,
    source_repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    target_repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    link_type TEXT NOT NULL,
    confidence DOUBLE PRECISION,
    note TEXT,
    created_at TEXT NOT NULL,
    UNIQUE(source_repository_id, target_repository_id, link_type)
);
CREATE INDEX IF NOT EXISTS idx_repository_links_source ON repository_links(source_repository_id);
CREATE INDEX IF NOT EXISTS idx_repository_links_target ON repository_links(target_repository_id);
