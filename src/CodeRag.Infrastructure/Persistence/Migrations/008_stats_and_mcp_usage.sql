-- 008: precomputed statistics snapshots + MCP tool usage tracking
--
-- repository_stats / global_stats replace on-the-fly COUNT/GROUP BY queries on
-- the hot path (dashboard polling, MCP `repositories` tool). Snapshots are
-- refreshed on startup and after indexing jobs complete.
--
-- mcp_tool_usage stores one row per MCP tool invocation for usage statistics.

PRAGMA foreign_keys = ON;

-- 1. per-repository statistics snapshot
CREATE TABLE IF NOT EXISTS repository_stats (
    repository_id TEXT PRIMARY KEY REFERENCES repositories(id) ON DELETE CASCADE,
    file_count INTEGER NOT NULL DEFAULT 0,
    symbol_count INTEGER NOT NULL DEFAULT 0,
    chunk_count INTEGER NOT NULL DEFAULT 0,
    vector_count INTEGER NOT NULL DEFAULT 0,
    relation_count INTEGER NOT NULL DEFAULT 0,
    relations_by_type TEXT,              -- JSON array [{"relationType":"Calls","count":3}]
    language_breakdown TEXT,             -- JSON object {"csharp":12,"python":3}
    embedding_code_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_code_pending INTEGER NOT NULL DEFAULT 0,
    embedding_text_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_text_pending INTEGER NOT NULL DEFAULT 0,
    embeddings_failed INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);

-- 2. global statistics snapshot (single row)
CREATE TABLE IF NOT EXISTS global_stats (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    repository_count INTEGER NOT NULL DEFAULT 0,
    file_count INTEGER NOT NULL DEFAULT 0,
    symbol_count INTEGER NOT NULL DEFAULT 0,
    chunk_count INTEGER NOT NULL DEFAULT 0,
    relation_count INTEGER NOT NULL DEFAULT 0,
    relations_by_type TEXT,              -- JSON array
    language_breakdown TEXT,             -- JSON object
    embedding_code_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_code_pending INTEGER NOT NULL DEFAULT 0,
    embedding_text_indexed INTEGER NOT NULL DEFAULT 0,
    embedding_text_pending INTEGER NOT NULL DEFAULT 0,
    embeddings_failed INTEGER NOT NULL DEFAULT 0,
    job_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);

-- 3. MCP tool usage log
CREATE TABLE IF NOT EXISTS mcp_tool_usage (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    at TEXT NOT NULL,                    -- UTC ISO 8601
    tool TEXT NOT NULL,
    status TEXT NOT NULL,                -- 'ok' | 'error'
    duration_ms INTEGER,
    repository_id TEXT
);
CREATE INDEX IF NOT EXISTS idx_mcp_tool_usage_at ON mcp_tool_usage(at);
CREATE INDEX IF NOT EXISTS idx_mcp_tool_usage_tool ON mcp_tool_usage(tool);

-- 4. language indexes (per-repo language breakdown is computed on refresh)
CREATE INDEX IF NOT EXISTS idx_files_language ON files(repository_id, language);
CREATE INDEX IF NOT EXISTS idx_chunks_language ON chunks(repository_id, language);
CREATE INDEX IF NOT EXISTS idx_symbols_language ON symbols(repository_id, language);
