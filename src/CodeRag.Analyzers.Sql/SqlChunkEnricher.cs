using System.Text;

namespace CodeRag.Analyzers.Sql;

/// <summary>
/// Извлекает семантические метаданные из SQL-источника и добавляет их
/// как префикс-заголовок к чанку. Это позволяет эмбеддинг-модели и FTS
/// сопоставлять естественные запросы ("где подготавливаются данные для выгрузки")
/// с SQL-процедурами, которые содержат эти концепции в извлечённых метаданных.
/// </summary>
public static class SqlChunkEnricher
{
    /// <summary>
    /// Создаёт обогащённый источник чанка: семантический заголовок + исходный SQL.
    /// </summary>
    public static string Enrich(string source, string? symbolName, string? symbolKind, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(source)) return source ?? string.Empty;

        var metadata = ExtractMetadata(source);

        var header = new StringBuilder();
        header.Append("-- CodeAtlas Semantic Index:");

        if (!string.IsNullOrWhiteSpace(symbolKind))
            header.Append($" Kind={symbolKind}");

        if (!string.IsNullOrWhiteSpace(symbolName))
            header.Append($" Procedure={symbolName}");

        if (!string.IsNullOrWhiteSpace(filePath))
            header.Append($" File={filePath}");

        if (metadata.Operations.Count > 0)
        {
            var ops = string.Join(",", metadata.Operations);
            header.Append($" Operations={ops}");
        }

        if (metadata.Tables.Count > 0)
        {
            var tables = string.Join(",", metadata.Tables);
            header.Append($" Tables={tables}");
        }

        if (metadata.Columns.Count > 0)
        {
            var meaningfulColumns = metadata.Columns.Where(c => c != "*").Take(10);
            if (meaningfulColumns.Any())
            {
                var cols = string.Join(",", meaningfulColumns);
                header.Append($" Columns={cols}");
            }
        }

        if (metadata.Parameters.Count > 0)
        {
            var params_ = string.Join(",", metadata.Parameters.Take(20));
            header.Append($" Parameters={params_}");
        }

        if (metadata.Joins.Count > 0)
        {
            var joins = string.Join(",", metadata.Joins);
            header.Append($" Joins={joins}");
        }

        header.Append("\n");
        header.Append(source);

        return header.ToString();
    }

    private static SqlMetadata ExtractMetadata(string source)
    {
        var metadata = new SqlMetadata();

        // Extract operations
        var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in new[] { "select", "insert", "update", "delete", "create", "alter", "drop", "merge", "exec", "execute" })
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(source, $@"(?i)\b{keyword}\b"))
                ops.Add(keyword.ToUpperInvariant());
        }
        metadata.Operations = ops;

        // Extract parameters (@paramName)
        var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, @"@(\w+)"))
        {
            var m = (System.Text.RegularExpressions.Match)match;
            var param = m.Groups[1].Value;
            if (param.Length > 0 && param.Length < 100)
                parameters.Add(param);
        }
        metadata.Parameters = parameters;

        // Extract table references
        ExtractTablesRegex(source, metadata);

        // Extract columns
        ExtractColumnsRegex(source, metadata);

        // Extract joins
        ExtractJoinsRegex(source, metadata);

        return metadata;
    }

    private static void ExtractTablesRegex(string source, SqlMetadata metadata)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pattern 1: FROM table_name or FROM schema.table_name (supports #temp tables)
        ExtractPattern(source, metadata, tables, @"(?:FROM|INTO|UPDATE)\s+([\w#]+\.)?([\w#]+)");
        
        // Pattern 2: JOIN table_name (supports #temp tables)
        ExtractPattern(source, metadata, tables, @"\bJOIN\s+([\w#]+\.)?([\w#]+)");
        
        // Pattern 3: INSERT INTO table_name (supports #temp tables)
        ExtractPattern(source, metadata, tables, @"\bINSERT\s+INTO\s+([\w#]+\.)?([\w#]+)");
        
        // Pattern 3b: DELETE FROM table_name (supports #temp tables)
        ExtractPattern(source, metadata, tables, @"\bDELETE\s+FROM\s+([\w#]+\.)?([\w#]+)");
        
        // Pattern 4: CREATE TABLE table_name or CREATE TABLE schema.table_name
        ExtractPattern(source, metadata, tables, @"\bCREATE\s+TABLE\s+([\w#]+\.)?([\w#]+)");
        
        // Pattern 5: ALTER TABLE table_name
        ExtractPattern(source, metadata, tables, @"\bALTER\s+TABLE\s+([\w#]+\.)?([\w#]+)");
        
        // Pattern 6: DROP TABLE table_name
        ExtractPattern(source, metadata, tables, @"\bDROP\s+TABLE\s+([\w#]+\.)?([\w#]+)");

        // Remove common non-table identifiers
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "select", "where", "set", "values", "and", "or", "not", "null",
            "begin", "end", "if", "else", "while", "return", "go", "declare",
            "procedure", "function", "view", "trigger", "index",
            "constraint", "default", "schema", "database", "server", "login",
            "user", "role", "grant", "revoke", "deny",
            "on", "off", "with", "as", "to", "from"
        };

        foreach (var t in tables.ToList())
        {
            if (excluded.Contains(t.ToLowerInvariant()))
                tables.Remove(t);
        }

        metadata.Tables = tables;
    }

    private static void ExtractPattern(string source, SqlMetadata metadata, HashSet<string> tables, string pattern)
    {
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, pattern))
        {
            var m = (System.Text.RegularExpressions.Match)match;
            string fullTable;
            
            if (m.Groups.Count >= 3 && !string.IsNullOrEmpty(m.Groups[2].Value))
            {
                var schema = m.Groups[1]?.Value ?? "";
                var table = m.Groups[2].Value;
                fullTable = (schema + table).Trim('[', ']', ' ', '\t');
            }
            else if (m.Groups.Count >= 2 && !string.IsNullOrEmpty(m.Groups[1].Value))
            {
                fullTable = m.Groups[1].Value.Trim('[', ']', ' ', '\t');
            }
            else
            {
                continue;
            }

            if (fullTable.Length > 0 && fullTable.Length < 100 && !tables.Contains(fullTable))
                tables.Add(fullTable);
        }
    }

    private static void ExtractColumnsRegex(string source, SqlMetadata metadata)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Look for column names in SELECT list
        // Pattern: SELECT col1, col2, col3 FROM ...
        var selectMatch = System.Text.RegularExpressions.Regex.Match(source, @"(?i)\bSELECT\s+(.*?)\s+FROM\b");
        if (selectMatch.Success)
        {
            var selectClause = selectMatch.Groups[1].Value;
            
            // Split by comma and extract column names
            foreach (var part in selectClause.Split(','))
            {
                var trimmed = part.Trim();
                
                // Skip aliases (col AS alias)
                var aliasMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^[\w.\[\]]+\s+AS\s+([\w]+)");
                if (aliasMatch.Success)
                {
                    columns.Add(aliasMatch.Groups[1].Value);
                    continue;
                }
                
                // Skip functions (COUNT(*), SUM(col), etc.)
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"(?i)^\w+\s*\("))
                    continue;
                
                // Skip *
                if (trimmed == "*")
                    continue;
                
                // Extract last identifier (the column name)
                var lastIdMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"[\[\]]?([\w]+)[\]\]]?$");
                if (lastIdMatch.Success && lastIdMatch.Groups[1].Value.Length > 0)
                {
                    columns.Add(lastIdMatch.Groups[1].Value);
                }
            }
        }

        metadata.Columns = columns;
    }

    private static void ExtractJoinsRegex(string source, SqlMetadata metadata)
    {
        var joins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pattern: JOIN table_name ON ...
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(source, @"\bJOIN\s+([\w]+\.)?([\w]+)\s+ON\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var m = (System.Text.RegularExpressions.Match)match;
            string fullTable;
            
            if (!string.IsNullOrEmpty(m.Groups[2].Value))
            {
                var schema = m.Groups[1]?.Value ?? "";
                var table = m.Groups[2].Value;
                fullTable = (schema + table).Trim('[', ']', ' ', '\t');
            }
            else
            {
                continue;
            }

            if (fullTable.Length > 0 && fullTable.Length < 100)
                joins.Add(fullTable);
        }

        metadata.Joins = joins;
    }

    private sealed class SqlMetadata
    {
        public HashSet<string> Operations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Joins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
