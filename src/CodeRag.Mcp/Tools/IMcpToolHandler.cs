namespace CodeRag.Mcp.Tools;

/// <summary>
/// Thin adapter fallback when ModelContextProtocol SDK unavailable offline.
/// Real MCP tools (ModelContextProtocol 2.2.0) implement same contracts via [McpServerTool].
/// </summary>
public interface IMcpToolHandler
{
    Task<string> RepositoriesAsync(CancellationToken ct = default);
    Task<string> SearchCodeAsync(string query, string? mode = null, int? limit = null, int? offset = null, Guid[]? repositoryIds = null, string? languages = null, string? symbolKind = null, double? minScore = null, CancellationToken ct = default);
    Task<string> GetCodeAsync(Guid repositoryId, string path, int? startLine = null, int? endLine = null, int? maxLines = null, CancellationToken ct = default);
    Task<string> FindSymbolAsync(string query, Guid? repositoryId = null, string? language = null, int? limit = null, CancellationToken ct = default);
    Task<string> GetSymbolAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> GetRelationsAsync(Guid symbolId, string? direction = null, string? relationTypes = null, int? limit = null, CancellationToken ct = default);
    Task<string> FindReferencesAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> FindCallersAsync(Guid symbolId, CancellationToken ct = default);
    Task<string> FindCalleesAsync(Guid symbolId, CancellationToken ct = default);
}
