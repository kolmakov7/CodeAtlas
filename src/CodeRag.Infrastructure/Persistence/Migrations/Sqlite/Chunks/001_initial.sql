-- CodeRag — SQLite CHUNKS database (consolidated, alpha initial)
-- Contains chunks + chunks_fts (FTS5 trigram).
-- No FKs to main-DB tables (repositories/files/symbols) — separate file;
-- referential cleanup is done explicitly by RepositoryStore.DeleteCascadeAsync.

PRAGMA foreign_keys = ON;

-- migrations tracking
CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    applied_at TEXT NOT NULL
);

-- chunks
CREATE TABLE IF NOT EXISTS chunks (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL,
    file_id TEXT NOT NULL,
    symbol_id TEXT,
    language TEXT,
    start_line INTEGER,
    end_line INTEGER,
    content_hash TEXT NOT NULL,
    source TEXT NOT NULL,
    embedding_model TEXT,
    embedding_config_hash TEXT,
    embedded_at TEXT,
    embedding_domain TEXT NOT NULL DEFAULT 'code'
);

CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
CREATE INDEX IF NOT EXISTS idx_chunks_hash ON chunks(content_hash);
CREATE INDEX IF NOT EXISTS idx_chunks_repo ON chunks(repository_id);
CREATE INDEX IF NOT EXISTS idx_chunks_domain ON chunks(embedding_domain);
CREATE INDEX IF NOT EXISTS idx_chunks_language ON chunks(repository_id, language);

-- FTS5 trigram index (manual sync via FtsHelper, no triggers)
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
