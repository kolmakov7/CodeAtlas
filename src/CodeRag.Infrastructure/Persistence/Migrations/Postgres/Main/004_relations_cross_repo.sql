-- CodeRag — PostgreSQL MAIN database: cross-repository relations (004)
ALTER TABLE relations ADD COLUMN IF NOT EXISTS from_repository_id TEXT;
ALTER TABLE relations ADD COLUMN IF NOT EXISTS to_repository_id TEXT;
ALTER TABLE relations ADD COLUMN IF NOT EXISTS origin_repository_id TEXT;

UPDATE relations SET
  from_repository_id = (SELECT s.repository_id FROM symbols s WHERE s.id = relations.from_symbol_id),
  to_repository_id   = (SELECT s.repository_id FROM symbols s WHERE s.id = relations.to_symbol_id)
WHERE from_repository_id IS NULL;

UPDATE relations SET origin_repository_id = repository_id WHERE origin_repository_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_relations_from_repo ON relations(from_repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_to_repo ON relations(to_repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_origin ON relations(origin_repository_id);
