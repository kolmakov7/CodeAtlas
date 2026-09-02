-- 006: job progress details for the UI.
-- total_items / items_done: unit depends on the job phase — files for Index/Rebuild, chunks for Embedding.
-- phase: human-readable stage name; current_file: last processed path; from/to_commit: index range (persisted for restored jobs).
ALTER TABLE indexing_jobs ADD COLUMN total_items INTEGER;
ALTER TABLE indexing_jobs ADD COLUMN items_done INTEGER;
ALTER TABLE indexing_jobs ADD COLUMN phase TEXT;
ALTER TABLE indexing_jobs ADD COLUMN current_file TEXT;
ALTER TABLE indexing_jobs ADD COLUMN from_commit TEXT;
ALTER TABLE indexing_jobs ADD COLUMN to_commit TEXT;
