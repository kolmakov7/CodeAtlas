-- 003: dual embedding domains (code / text).
-- Tracks which embedding space each chunk and vector belongs to, so the code and text
-- vector spaces stay isolated and can be rebuilt/invalidated independently.
ALTER TABLE chunks ADD COLUMN embedding_domain TEXT NOT NULL DEFAULT 'code';
ALTER TABLE vector_entries ADD COLUMN domain TEXT NOT NULL DEFAULT 'code';

CREATE INDEX IF NOT EXISTS idx_chunks_domain ON chunks(embedding_domain);
CREATE INDEX IF NOT EXISTS idx_vector_entries_domain ON vector_entries(domain);
