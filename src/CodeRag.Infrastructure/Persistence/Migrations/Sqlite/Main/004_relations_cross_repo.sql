-- CodeRag — SQLite MAIN database: cross-repository relations (004)
-- Adds explicit endpoint ownership + origin so a repository's re-index can safely remove only
-- the edges it produced, and stats/queries can count inbound cross-repo references.
ALTER TABLE relations ADD COLUMN from_repository_id TEXT;
ALTER TABLE relations ADD COLUMN to_repository_id TEXT;
ALTER TABLE relations ADD COLUMN origin_repository_id TEXT;

UPDATE relations SET
  from_repository_id = (SELECT s.repository_id FROM symbols s WHERE s.id = relations.from_symbol_id),
  to_repository_id   = (SELECT s.repository_id FROM symbols s WHERE s.id = relations.to_symbol_id);

UPDATE relations SET origin_repository_id = repository_id;

CREATE INDEX IF NOT EXISTS idx_relations_from_repo ON relations(from_repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_to_repo ON relations(to_repository_id);
CREATE INDEX IF NOT EXISTS idx_relations_origin ON relations(origin_repository_id);
