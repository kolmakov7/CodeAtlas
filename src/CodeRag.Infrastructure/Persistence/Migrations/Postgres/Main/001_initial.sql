-- CodeRag — PostgreSQL MAIN database (alpha initial)
-- Contains everything EXCEPT chunks / chunks_fts (they live in the <base>_chunks database).
-- vector_entries.chunk_id has no FK (chunks are in a separate database);
-- repository_id is denormalized for per-repo deletes/statistics.
-- Timestamps are stored as ISO-8601 TEXT to keep provider mapping identical to SQLite.

-- migrations tracking
CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    applied_at TEXT NOT NULL
);

-- proxies
CREATE TABLE IF NOT EXISTS proxies (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    scheme TEXT NOT NULL,
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    username TEXT,
    password_encrypted TEXT
);

-- credentials
CREATE TABLE IF NOT EXISTS credentials (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    username TEXT,
    secret_encrypted TEXT,
    ssh_key_path TEXT
);

-- repository groups (Repository -> Group -> Proxy/Credentials)
CREATE TABLE IF NOT EXISTS repository_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    proxy_id TEXT REFERENCES proxies(id) ON DELETE SET NULL,
    credential_id TEXT REFERENCES credentials(id) ON DELETE SET NULL,
    created_at TEXT NOT NULL
);

-- Seed Default group if not exists
INSERT INTO repository_groups(id, name, proxy_id, credential_id, created_at)
VALUES ('00000000-0000-0000-0000-000000000001', 'Default', NULL, NULL, to_char(now(), 'YYYY-MM-DD"T"HH24:MI:SSOF'))
ON CONFLICT (id) DO NOTHING;

-- repositories
CREATE TABLE IF NOT EXISTS repositories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    git_url TEXT NOT NULL,
    branch TEXT NOT NULL DEFAULT 'main',
    local_path TEXT NOT NULL,
    current_commit TEXT,
    indexed_commit TEXT,
    status TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    group_id TEXT REFERENCES repository_groups(id) ON DELETE SET NULL,
    proxy_id TEXT REFERENCES proxies(id),
    credential_id TEXT REFERENCES credentials(id),
    sync_interval_seconds INTEGER,
    last_sync_at TEXT,
    last_index_at TEXT,
    last_error TEXT,
    filter_enabled INTEGER NOT NULL DEFAULT 0,
    allowed_directories TEXT
);

-- projects
CREATE TABLE IF NOT EXISTS projects (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    name TEXT,
    path TEXT,
    language TEXT
);

-- files
CREATE TABLE IF NOT EXISTS files (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    project_id TEXT,
    path TEXT NOT NULL,
    language TEXT,
    hash TEXT,
    last_indexed_at TEXT,
    UNIQUE(repository_id, path),
    FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL
);

-- symbols
CREATE TABLE IF NOT EXISTS symbols (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL,
    project_id TEXT,
    file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    language TEXT NOT NULL,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    fqn TEXT,
    "namespace" TEXT,
    parent_symbol_id TEXT REFERENCES symbols(id) ON DELETE SET NULL,
    start_line INTEGER,
    end_line INTEGER,
    start_col INTEGER,
    end_col INTEGER,
    signature TEXT,
    FOREIGN KEY(repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL
);

-- relations (repository_id for per-repo stats / reconciliation / deletion)
CREATE TABLE IF NOT EXISTS relations (
    from_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    to_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    relation_type TEXT NOT NULL,
    repository_id TEXT,
    PRIMARY KEY(from_symbol_id, to_symbol_id, relation_type)
);

-- indexing jobs (progress details for the UI)
CREATE TABLE IF NOT EXISTS indexing_jobs (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT,
    progress REAL,
    error TEXT,
    total_items INTEGER,
    items_done INTEGER,
    phase TEXT,
    current_file TEXT,
    from_commit TEXT,
    to_commit TEXT
);

-- settings
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT
);

-- activity log
CREATE TABLE IF NOT EXISTS activity_log (
    id TEXT PRIMARY KEY,
    at TEXT NOT NULL,
    actor TEXT,
    action TEXT NOT NULL,
    repository_id TEXT,
    detail TEXT,
    FOREIGN KEY(repository_id) REFERENCES repositories(id) ON DELETE SET NULL
);

-- indexing errors
CREATE TABLE IF NOT EXISTS indexing_errors (
    id TEXT PRIMARY KEY,
    repository_id TEXT,
    file_path TEXT,
    message TEXT,
    at TEXT,
    FOREIGN KEY(repository_id) REFERENCES repositories(id) ON DELETE SET NULL
);

-- vector entries (metadata: ChunkId -> vector, per embedding domain).
-- chunk_id has no FK: chunks live in the separate chunks database.
CREATE TABLE IF NOT EXISTS vector_entries (
    chunk_id TEXT PRIMARY KEY,
    repository_id TEXT,
    vector_id TEXT NOT NULL UNIQUE,
    model TEXT NOT NULL,
    dim INTEGER NOT NULL,
    context_length INTEGER,
    domain TEXT NOT NULL DEFAULT 'code',
    updated_at TEXT
);

-- precomputed per-repository statistics snapshot
CREATE TABLE IF NOT EXISTS repository_stats (
    repository_id TEXT PRIMARY KEY REFERENCES repositories(id) ON DELETE CASCADE,
    file_count INTEGER NOT NULL DEFAULT 0,
    symbol_count INTEGER NOT NULL DEFAULT 0,
    chunk_count INTEGER NOT NULL DEFAULT 0,
    vector_count INTEGER NOT NULL DEFAULT 0,
    relation_count INTEGER NOT NULL DEFAULT 0,
    relations_by_type TEXT,
    language_breakdown TEXT,
    embedding_code_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_code_pending INTEGER NOT NULL DEFAULT 0,
    embedding_text_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_text_pending INTEGER NOT NULL DEFAULT 0,
    embeddings_failed INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);

-- global statistics snapshot (single row)
CREATE TABLE IF NOT EXISTS global_stats (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    repository_count INTEGER NOT NULL DEFAULT 0,
    file_count INTEGER NOT NULL DEFAULT 0,
    symbol_count INTEGER NOT NULL DEFAULT 0,
    chunk_count INTEGER NOT NULL DEFAULT 0,
    relation_count INTEGER NOT NULL DEFAULT 0,
    relations_by_type TEXT,
    language_breakdown TEXT,
    embedding_code_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_code_pending INTEGER NOT NULL DEFAULT 0,
    embedding_text_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_text_pending INTEGER NOT NULL DEFAULT 0,
    embeddings_failed INTEGER NOT NULL DEFAULT 0,
    job_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);

-- MCP tool usage log
CREATE TABLE IF NOT EXISTS mcp_tool_usage (
    id BIGSERIAL PRIMARY KEY,
    at TEXT NOT NULL,
    tool TEXT NOT NULL,
    status TEXT NOT NULL,
    duration_ms INTEGER,
    repository_id TEXT
);
CREATE INDEX IF NOT EXISTS idx_mcp_tool_usage_at ON mcp_tool_usage(at);
CREATE INDEX IF NOT EXISTS idx_mcp_tool_usage_tool ON mcp_tool_usage(tool);

-- Indexes
CREATE INDEX IF NOT EXISTS idx_symbols_repo_fqn ON symbols(repository_id, fqn);
CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
CREATE INDEX IF NOT EXISTS idx_symbols_repo ON symbols(repository_id);
CREATE INDEX IF NOT EXISTS idx_files_repo ON files(repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_to ON relations(to_symbol_id);
CREATE INDEX IF NOT EXISTS idx_projects_repo ON projects(repository_id);
CREATE INDEX IF NOT EXISTS idx_vector_entries_vector_id ON vector_entries(vector_id);
CREATE INDEX IF NOT EXISTS idx_vector_entries_repo ON vector_entries(repository_id);
CREATE INDEX IF NOT EXISTS idx_vector_entries_domain ON vector_entries(domain);
CREATE INDEX IF NOT EXISTS idx_repositories_group ON repositories(group_id);
CREATE INDEX IF NOT EXISTS idx_repository_groups_proxy ON repository_groups(proxy_id);
CREATE INDEX IF NOT EXISTS idx_repository_groups_credential ON repository_groups(credential_id);
CREATE INDEX IF NOT EXISTS idx_relations_repo ON relations(repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_repo_type ON relations(repository_id, relation_type);
CREATE INDEX IF NOT EXISTS idx_files_language ON files(repository_id, language);
CREATE INDEX IF NOT EXISTS idx_symbols_language ON symbols(repository_id, language);

-- Note: chunks / chunks_fts live in the separate <base>_chunks database (Migrations/Postgres/Chunks).
-- Note: pgvector tables (vec_chunks_code / vec_chunks_text) are created dynamically by
-- PgVectorVectorStore in the separate <base>_vec database.
