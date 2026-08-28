-- CodeRag Phase 1 Storage — 001_initial
-- SQLite WAL, FTS5 trigram, vector_entries, schema_migrations

PRAGMA foreign_keys = ON;

-- 0. migrations tracking
CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    applied_at TEXT NOT NULL
);

-- 1. proxies
CREATE TABLE IF NOT EXISTS proxies (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    scheme TEXT NOT NULL,
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    username TEXT,
    password_encrypted TEXT
);

-- 2. credentials
CREATE TABLE IF NOT EXISTS credentials (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    username TEXT,
    secret_encrypted TEXT,
    ssh_key_path TEXT
);

-- 3. repositories
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
    proxy_id TEXT REFERENCES proxies(id),
    credential_id TEXT REFERENCES credentials(id),
    sync_interval_seconds INTEGER,
    last_sync_at TEXT,
    last_index_at TEXT,
    last_error TEXT
);

-- 4. projects
CREATE TABLE IF NOT EXISTS projects (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    name TEXT,
    path TEXT,
    language TEXT
);

-- 5. files
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

-- 6. symbols
CREATE TABLE IF NOT EXISTS symbols (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL,
    project_id TEXT,
    file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    language TEXT NOT NULL,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    fqn TEXT,
    namespace TEXT,
    parent_symbol_id TEXT REFERENCES symbols(id) ON DELETE SET NULL,
    start_line INTEGER,
    end_line INTEGER,
    start_col INTEGER,
    end_col INTEGER,
    signature TEXT,
    FOREIGN KEY(repository_id) REFERENCES repositories(id) ON DELETE CASCADE,
    FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL
);

-- 7. relations
CREATE TABLE IF NOT EXISTS relations (
    from_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    to_symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    relation_type TEXT NOT NULL,
    PRIMARY KEY(from_symbol_id, to_symbol_id, relation_type)
);

-- 8. chunks
CREATE TABLE IF NOT EXISTS chunks (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    symbol_id TEXT REFERENCES symbols(id) ON DELETE SET NULL,
    language TEXT,
    start_line INTEGER,
    end_line INTEGER,
    content_hash TEXT NOT NULL,
    source TEXT NOT NULL,
    embedding_model TEXT,
    embedding_config_hash TEXT,
    embedded_at TEXT
);

-- 9. indexing_jobs
CREATE TABLE IF NOT EXISTS indexing_jobs (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT,
    progress REAL,
    error TEXT
);

-- 10. settings
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TEXT
);

-- 11. activity_log
CREATE TABLE IF NOT EXISTS activity_log (
    id TEXT PRIMARY KEY,
    at TEXT NOT NULL,
    actor TEXT,
    action TEXT NOT NULL,
    repository_id TEXT,
    detail TEXT,
    FOREIGN KEY(repository_id) REFERENCES repositories(id) ON DELETE SET NULL
);

-- 12. indexing_errors
CREATE TABLE IF NOT EXISTS indexing_errors (
    id TEXT PRIMARY KEY,
    repository_id TEXT,
    file_path TEXT,
    message TEXT,
    at TEXT,
    FOREIGN KEY(repository_id) REFERENCES repositories(id) ON DELETE SET NULL
);

-- 13. vector_entries (mapping ChunkId -> VectorId)
CREATE TABLE IF NOT EXISTS vector_entries (
    chunk_id TEXT PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
    vector_id TEXT NOT NULL UNIQUE,
    model TEXT NOT NULL,
    dim INTEGER NOT NULL,
    updated_at TEXT
);

-- Indexes (storage.md §2)
CREATE INDEX IF NOT EXISTS idx_symbols_repo_fqn ON symbols(repository_id, fqn);
CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
CREATE INDEX IF NOT EXISTS idx_symbols_repo ON symbols(repository_id);
CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
CREATE INDEX IF NOT EXISTS idx_chunks_hash ON chunks(content_hash);
CREATE INDEX IF NOT EXISTS idx_chunks_repo ON chunks(repository_id);
CREATE INDEX IF NOT EXISTS idx_files_repo ON files(repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_to ON relations(to_symbol_id);
CREATE INDEX IF NOT EXISTS idx_projects_repo ON projects(repository_id);
CREATE INDEX IF NOT EXISTS idx_vector_entries_vector_id ON vector_entries(vector_id);

-- FTS5
CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
    chunk_id UNINDEXED,
    repository_id UNINDEXED,
    content,
    symbol_name,
    fqn,
    file_path,
    namespace,
    signature,
    tokenize='trigram'
);

-- Note: vec_chunks virtual table is created dynamically by SqliteVecVectorStore
-- if sqlite-vec extension is available:
-- CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
--   chunk_id TEXT PRIMARY KEY,
--   embedding FLOAT[384] distance_metric=cosine,
--   repository_id TEXT
-- );
