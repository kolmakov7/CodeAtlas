CREATE TABLE IF NOT EXISTS consolidation_jobs (
    id TEXT PRIMARY KEY,
    type TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'ready_for_analysis',
    reason TEXT NOT NULL,
    project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
    repository_id TEXT REFERENCES repositories(id) ON DELETE SET NULL,
    dedupe_key TEXT NOT NULL UNIQUE,
    input_version TEXT NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    last_error TEXT,
    analysis TEXT,
    proposal TEXT,
    proposal_version INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    resolved_at TEXT,
    resolved_by TEXT,
    result_knowledge_id TEXT,
    version INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_consolidation_status ON consolidation_jobs(status, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_consolidation_scope ON consolidation_jobs(project_id, repository_id);
CREATE INDEX IF NOT EXISTS idx_consolidation_type ON consolidation_jobs(type);

CREATE TABLE IF NOT EXISTS consolidation_job_items (
    job_id TEXT NOT NULL REFERENCES consolidation_jobs(id) ON DELETE CASCADE,
    knowledge_id TEXT NOT NULL REFERENCES knowledge_entries(id) ON DELETE CASCADE,
    role TEXT NOT NULL DEFAULT 'source',
    version_at_creation INTEGER NOT NULL,
    content_hash_at_creation TEXT NOT NULL,
    previous_status TEXT NOT NULL DEFAULT 'active',
    PRIMARY KEY (job_id, knowledge_id)
);

CREATE INDEX IF NOT EXISTS idx_consolidation_items_knowledge ON consolidation_job_items(knowledge_id);

CREATE TABLE IF NOT EXISTS revision_runs (
    id TEXT PRIMARY KEY,
    trigger_type TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at TEXT NOT NULL,
    finished_at TEXT,
    stats TEXT,
    error TEXT
);

CREATE INDEX IF NOT EXISTS idx_revision_runs_started ON revision_runs(started_at DESC);
