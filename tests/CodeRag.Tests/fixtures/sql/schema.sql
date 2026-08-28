-- Placeholder SQL fixture — procedures, views, tables
CREATE TABLE clients (id INT PRIMARY KEY, is_active BIT);
CREATE VIEW active_clients AS SELECT * FROM clients WHERE is_active = 1;
CREATE PROCEDURE DeactivateClient(@id INT) AS UPDATE clients SET is_active = 0 WHERE id = @id;
