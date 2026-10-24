CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS knowledge_entries (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    type TEXT NOT NULL DEFAULT 'fact',
    project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
    repository_id TEXT REFERENCES repositories(id) ON DELETE SET NULL,
    status TEXT NOT NULL DEFAULT 'active',
    source TEXT NOT NULL DEFAULT 'agent',
    source_ref TEXT,
    external_key TEXT,
    content_hash TEXT NOT NULL,
    created_by TEXT,
    metadata TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    valid_from TEXT,
    valid_until TEXT,
    version INTEGER NOT NULL DEFAULT 1,
    embedded_model TEXT,
    embedded_config_hash TEXT,
    embedded_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_knowledge_project_status ON knowledge_entries(project_id, status);
CREATE INDEX IF NOT EXISTS idx_knowledge_repository_status ON knowledge_entries(repository_id, status);
CREATE INDEX IF NOT EXISTS idx_knowledge_type ON knowledge_entries(type);
CREATE INDEX IF NOT EXISTS idx_knowledge_updated ON knowledge_entries(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_knowledge_external ON knowledge_entries(external_key);
CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledge_external_project
    ON knowledge_entries(project_id, external_key) WHERE external_key IS NOT NULL AND project_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledge_external_global
    ON knowledge_entries(external_key) WHERE external_key IS NOT NULL AND project_id IS NULL;

CREATE TABLE IF NOT EXISTS knowledge_fts (
    knowledge_id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    content TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_knowledge_fts_title_trgm ON knowledge_fts USING gin (title gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_knowledge_fts_content_trgm ON knowledge_fts USING gin (content gin_trgm_ops);

ALTER TABLE vector_entries ADD COLUMN IF NOT EXISTS owner_type TEXT NOT NULL DEFAULT 'chunk';
CREATE INDEX IF NOT EXISTS idx_vector_entries_owner ON vector_entries(owner_type);
