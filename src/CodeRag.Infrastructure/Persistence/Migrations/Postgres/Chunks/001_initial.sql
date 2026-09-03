-- CodeRag — PostgreSQL CHUNKS database (alpha initial)
-- Contains chunks + chunks_fts (regular table + pg_trgm GIN indexes).
-- No FKs to main-DB tables (repositories/files/symbols) — separate database;
-- referential cleanup is done explicitly by RepositoryStore.DeleteCascadeAsync.

CREATE EXTENSION IF NOT EXISTS pg_trgm;

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

-- Full-text search table (mirrors the SQLite FTS5 schema). Manual sync via FtsHelper.
-- pg_trgm GIN indexes power ILIKE '%term%' substring matching (trigram similarity).
CREATE TABLE IF NOT EXISTS chunks_fts (
    chunk_id TEXT PRIMARY KEY,
    repository_id TEXT,
    content TEXT,
    symbol_name TEXT,
    fqn TEXT,
    file_path TEXT,
    "namespace" TEXT,
    signature TEXT
);

CREATE INDEX IF NOT EXISTS idx_chunks_fts_repo ON chunks_fts(repository_id);
CREATE INDEX IF NOT EXISTS idx_chunks_fts_content_trgm ON chunks_fts USING gin (content gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_chunks_fts_symbol_trgm ON chunks_fts USING gin (symbol_name gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_chunks_fts_fqn_trgm ON chunks_fts USING gin (fqn gin_trgm_ops);
