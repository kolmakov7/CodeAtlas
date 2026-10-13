namespace CodeRag.Mcp;

/// <summary>
/// Workflow guidance sent in the MCP initialize response. Tool-specific details live
/// on the tools themselves so clients can discover the complete contract from MCP.
/// </summary>
public static class McpServerGuidance
{
    public const string Instructions = """
        CodeAtlas provides indexed code intelligence across repositories: hybrid, semantic,
        full-text and symbol search plus source and relationship navigation.

        Recommended workflow:
        1. Call repositories first when the repository or repositoryId is unknown.
        2. Locate code with search_code (Hybrid is the best default) or find_symbol.
        3. Use the returned repositoryId and relative file path with get_code; use symbolId
           with get_symbol and the relationship tools.
        4. For navigation, chain symbolId -> get_symbol -> get_relations or one of
           find_callers, find_callees, find_references, find_implementations, then inspect
           related symbols with get_symbol or get_code.

        Search mode selection:
        - Hybrid: best first search; combines lexical, semantic, exact and structural signals.
        - SemanticCode: natural-language intent over source-code embeddings only.
        - SemanticText: natural-language questions over documentation and prose only.
        - FullText: exact identifiers and tokens.
        - Symbol: exact symbol name or fully-qualified name lookup.
        - Semantic: compatibility mode merging code and text vectors; prefer the explicit
          SemanticCode or SemanticText modes when the corpus is known.

        Only indexed content is available. Repository and symbol identifiers are GUIDs.
        Search results expose score and origin; weak semantic queries can legitimately return
        no results, so prefer Hybrid or FullText for ambiguous identifiers and narrow searches
        with repositoryIds, languages, symbolKind or minScore when appropriate.
        """;
}
