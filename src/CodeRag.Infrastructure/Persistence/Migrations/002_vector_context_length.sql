-- 002: store embedding model context length (n_ctx, tokens) next to dim in vector_entries.
-- Enables detecting stale index when the model / API link / context length changes.
ALTER TABLE vector_entries ADD COLUMN context_length INTEGER;
