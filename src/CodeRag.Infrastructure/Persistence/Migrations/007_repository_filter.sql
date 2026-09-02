-- 007: repository directory filter — branch indexing scope
-- Adds filter_enabled (bool) and allowed_directories (JSON array string)

ALTER TABLE repositories ADD COLUMN filter_enabled INTEGER NOT NULL DEFAULT 0;
ALTER TABLE repositories ADD COLUMN allowed_directories TEXT;

-- Ensure existing rows have consistent null for allowed_directories when disabled
UPDATE repositories SET allowed_directories = NULL WHERE filter_enabled = 0;
