namespace CodeRag.Mcp;

/// <summary>
/// Workflow guidance sent in the MCP initialize response. Keep this intentionally short:
/// verbose per-tool instructions are listed only when the tools are exposed, because the
/// guidance consumes context in every session.
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
        4. Navigate with find_callers, find_callees, find_references, find_implementations or
           get_relations.

        Search modes: Hybrid (default), SemanticCode, SemanticText, FullText, Symbol, Semantic.
        Only indexed content is available. Identifiers are GUIDs. Prefer Hybrid or FullText for
        ambiguous identifiers and narrow searches with repositoryIds/languages/symbolKind.

        Central tasks and knowledge:
        - Use task tools instead of writing TODO_*.md files; the server is the source of truth.
        - Pass expectedVersion on updates. On a version conflict, re-read and retry; never
          overwrite blindly.
        - Pass externalKey for idempotent task/knowledge creation.
        - Store durable decisions, constraints and research with save_knowledge, separately
          from temporary tasks, and link it to tasks/symbols/files.
        """;

    public const string RevisionInstructions = """

        Knowledge revision (enabled explicitly):
        - run_revision only inventories knowledge and queues jobs. It never calls the LLM and
          never applies changes.
        - Call analyze_revision_job only on an explicit user request. If the LLM is not
          configured, report that dependency state and stop; do not retry in a loop.
        - accept_revision_job requires the current proposalVersion from get_revision_job.
        """;
}
