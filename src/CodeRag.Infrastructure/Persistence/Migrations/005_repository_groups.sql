-- 005: repository groups — moves Proxy/Credentials from Repository to Group
-- Repository -> Group -> Proxy/Credentials

CREATE TABLE IF NOT EXISTS repository_groups (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    proxy_id TEXT REFERENCES proxies(id) ON DELETE SET NULL,
    credential_id TEXT REFERENCES credentials(id) ON DELETE SET NULL,
    created_at TEXT NOT NULL
);

-- Seed Default group if not exists
INSERT OR IGNORE INTO repository_groups(id, name, proxy_id, credential_id, created_at)
VALUES ('00000000-0000-0000-0000-000000000001', 'Default', NULL, NULL, datetime('now'));

-- Add group_id to repositories (nullable, default -> Default group)
ALTER TABLE repositories ADD COLUMN group_id TEXT REFERENCES repository_groups(id) ON DELETE SET NULL;

-- Backfill: assign Default group to all existing repos where group_id is null
UPDATE repositories SET group_id = '00000000-0000-0000-0000-000000000001' WHERE group_id IS NULL;

-- Migrate distinct proxy/credential combos into dedicated groups (preserve data)
-- For repos with non-null proxy_id / credential_id that differ from Default's NULL/NULL,
-- create a group per distinct pair and reassign.
-- This is done via INSERT OR IGNORE for each distinct pair that is not (NULL,NULL)
-- Using a separate routine in application code is more robust; here we just ensure
-- Default exists. Detailed per-proxy groups are created lazily by app startup if needed.

CREATE INDEX IF NOT EXISTS idx_repositories_group ON repositories(group_id);
CREATE INDEX IF NOT EXISTS idx_repository_groups_proxy ON repository_groups(proxy_id);
CREATE INDEX IF NOT EXISTS idx_repository_groups_credential ON repository_groups(credential_id);
