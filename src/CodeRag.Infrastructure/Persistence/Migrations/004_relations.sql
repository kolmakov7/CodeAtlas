-- 004: relations — add owning repository for per-repo statistics, per-language
-- reconciliation (C# full-repo extractor) and per-repo deletion.
-- The relations table already exists (001) with PK (from_symbol_id, to_symbol_id, relation_type)
-- and ON DELETE CASCADE FKs to symbols. We add repository_id so per-repository relation
-- statistics and per-language rebuilds can run without walking the symbols table.

ALTER TABLE relations ADD COLUMN repository_id TEXT;

-- Backfill from the "from" symbol's repository (relations are intra-repository by construction).
UPDATE relations
SET repository_id = (SELECT s.repository_id FROM symbols s WHERE s.id = relations.from_symbol_id)
WHERE repository_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_relations_repo ON relations(repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_repo_type ON relations(repository_id, relation_type);
