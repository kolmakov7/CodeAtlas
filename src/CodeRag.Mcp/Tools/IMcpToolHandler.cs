namespace CodeRag.Mcp.Tools;

/// <summary>
/// Thin adapter fallback when ModelContextProtocol SDK unavailable offline.
/// Real MCP tools (ModelContextProtocol 2.2.0) implement same contracts via [McpServerTool].
/// </summary>
public interface IMcpToolHandler
{
    Task<string> SearchCodeAsync(string query, string? mode, int? limit, int? offset, Guid[]? repositoryIds, CancellationToken ct = default);
    Task<string> GetCodeAsync(Guid repositoryId, string path, int? startLine, int? endLine, CancellationToken ct = default);
    Task<string> FindSymbolAsync(string query, Guid? repositoryId, string? language, int? limit, CancellationToken ct = default);
    Task<string> FindReferencesAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> FindCallersAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> FindCalleesAsync(Guid symbolId, CancellationToken ct = default);
}
