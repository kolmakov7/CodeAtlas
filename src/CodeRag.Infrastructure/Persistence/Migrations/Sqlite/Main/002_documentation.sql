-- CodeRag — SQLite MAIN database: generated documentation (002)
-- LLM-generated Markdown per target (repository / module / symbol).
-- Identity: (repository_id, target_type, target_ref). target_id (symbol id) is
-- convenience metadata that may change across re-indexes.

CREATE TABLE IF NOT EXISTS documentation (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    target_type TEXT NOT NULL,
    target_id TEXT,
    target_ref TEXT NOT NULL,
    name TEXT NOT NULL,
    namespace TEXT,
    file_path TEXT,
    language TEXT,
    content TEXT NOT NULL,
    model TEXT,
    prompt_version TEXT NOT NULL DEFAULT 'v1',
    source_hash TEXT,
    status TEXT NOT NULL,
    error TEXT,
    generation_commit TEXT,
    context_chars INTEGER,
    output_chars INTEGER,
    generated_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(repository_id, target_type, target_ref)
);

CREATE INDEX IF NOT EXISTS idx_documentation_repo ON documentation(repository_id);
CREATE INDEX IF NOT EXISTS idx_documentation_repo_type ON documentation(repository_id, target_type);
