DELETE FROM relations
WHERE from_symbol_id NOT IN (SELECT id FROM symbols)
   OR to_symbol_id NOT IN (SELECT id FROM symbols);

DELETE FROM symbols WHERE repository_id NOT IN (SELECT id FROM repositories);
UPDATE symbols SET parent_symbol_id = NULL
WHERE parent_symbol_id IS NOT NULL AND parent_symbol_id NOT IN (SELECT id FROM symbols);

DELETE FROM files WHERE repository_id NOT IN (SELECT id FROM repositories);
UPDATE files SET project_id = NULL
WHERE project_id IS NOT NULL AND project_id NOT IN (SELECT id FROM projects);

DELETE FROM indexing_errors
WHERE repository_id IS NOT NULL AND repository_id NOT IN (SELECT id FROM repositories);

DELETE FROM documentation WHERE repository_id NOT IN (SELECT id FROM repositories);
