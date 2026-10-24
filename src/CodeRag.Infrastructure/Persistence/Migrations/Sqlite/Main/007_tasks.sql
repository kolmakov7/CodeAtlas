CREATE TABLE IF NOT EXISTS project_repositories (
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    repository_id TEXT NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    created_at TEXT NOT NULL,
    PRIMARY KEY (project_id, repository_id)
);

INSERT OR IGNORE INTO project_repositories(project_id, repository_id, created_at)
SELECT id, repository_id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now') FROM projects;

CREATE INDEX IF NOT EXISTS idx_project_repositories_repo ON project_repositories(repository_id);

CREATE TABLE IF NOT EXISTS tasks (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    description TEXT,
    kind TEXT NOT NULL DEFAULT 'work',
    status TEXT NOT NULL DEFAULT 'open',
    previous_status TEXT,
    priority INTEGER NOT NULL DEFAULT 2,
    project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
    repository_id TEXT REFERENCES repositories(id) ON DELETE SET NULL,
    parent_task_id TEXT REFERENCES tasks(id) ON DELETE SET NULL,
    source TEXT NOT NULL DEFAULT 'agent',
    external_key TEXT,
    created_by TEXT,
    metadata TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    archived_at TEXT,
    version INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_tasks_project_status ON tasks(project_id, status);
CREATE INDEX IF NOT EXISTS idx_tasks_repository_status ON tasks(repository_id, status);
CREATE INDEX IF NOT EXISTS idx_tasks_parent ON tasks(parent_task_id);
CREATE INDEX IF NOT EXISTS idx_tasks_updated ON tasks(updated_at DESC);
CREATE UNIQUE INDEX IF NOT EXISTS ux_tasks_external_project
    ON tasks(project_id, external_key) WHERE external_key IS NOT NULL AND project_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_tasks_external_global
    ON tasks(external_key) WHERE external_key IS NOT NULL AND project_id IS NULL;

CREATE TABLE IF NOT EXISTS task_comments (
    id TEXT PRIMARY KEY,
    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    author TEXT,
    body TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_task_comments_task ON task_comments(task_id, created_at);

CREATE TABLE IF NOT EXISTS entity_links (
    id TEXT PRIMARY KEY,
    from_type TEXT NOT NULL,
    from_id TEXT NOT NULL,
    to_type TEXT NOT NULL,
    to_id TEXT NOT NULL,
    relation TEXT NOT NULL,
    note TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL,
    UNIQUE(from_type, from_id, to_type, to_id, relation)
);

CREATE INDEX IF NOT EXISTS idx_entity_links_from ON entity_links(from_type, from_id);
CREATE INDEX IF NOT EXISTS idx_entity_links_to ON entity_links(to_type, to_id);
CREATE INDEX IF NOT EXISTS idx_entity_links_relation ON entity_links(relation);

CREATE TABLE IF NOT EXISTS entity_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    at TEXT NOT NULL,
    actor TEXT,
    action TEXT NOT NULL,
    version_from INTEGER,
    version_to INTEGER,
    changes TEXT
);

CREATE INDEX IF NOT EXISTS idx_entity_history_entity ON entity_history(entity_type, entity_id, at DESC);
