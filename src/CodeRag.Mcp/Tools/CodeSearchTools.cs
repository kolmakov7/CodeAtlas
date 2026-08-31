using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CodeRag.Search;
using CodeRag.Search.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSearchTools : IMcpToolHandler
{
    private readonly ISearchService _search;
    private readonly ILogger<CodeSearchTools> _logger;
    private const int PreviewLimit = 20;

    public CodeSearchTools(ISearchService search, ILogger<CodeSearchTools> logger)
    {
        _search = search;
        _logger = logger;
    }

    [McpServerTool, Description("Code search (Hybrid by default: semantic + FullText + exact; Semantic = vectors only; FullText = lexical only). Returns compact previews ≤limit lines. Use to find code by query.")]
    public async Task<string> SearchCodeAsync(
        [Description("Search query text")] string query,
        [Description("Mode: Hybrid|FullText|Semantic|Symbol (default Hybrid)")] string? mode = null,
        [Description("Max results (1..100, default 20)")] int? limit = null,
        [Description("Offset for pagination (default 0)")] int? offset = null,
        [Description("Optional repository filter (Guid array)")] Guid[]? repositoryIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query must not be empty";
        var m = ParseMode(mode);
        var req = new SearchRequest(query, repositoryIds, null, null, m, limit ?? 20, offset ?? 0);
        _logger.LogInformation("MCP search_code query='{Query}' mode={Mode} limit={Limit}", TruncateLog(query), m, limit);
        var result = await _search.SearchCodeAsync(req, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"Total candidates: {result.Total}, returned: {result.Items.Count}, elapsedMs: {result.ElapsedMs}");
        if (result.Warnings.Count > 0) sb.AppendLine($"Warnings: {string.Join(", ", result.Warnings)}");
        foreach (var item in result.Items)
        {
            var preview = Truncate(item.Preview, PreviewLimit);
            sb.AppendLine($"- repo:{item.RepositoryName ?? item.RepositoryId.ToString()} file:{item.FilePath} lines:{item.StartLine}-{item.EndLine} score:{item.Score:F3} origin:{item.Origin}");
            if (!string.IsNullOrEmpty(item.SymbolName)) sb.AppendLine($"  symbol: {item.SymbolName} ({item.Fqn})");
            sb.AppendLine($"  preview:\n{Indent(preview)}");
        }
        return sb.ToString();
    }

    // MCP alias: search_code -> SearchCode
    [McpServerTool(Name = "search_code"), Description("Alias for SearchCode")]
    public Task<string> SearchCode(string query, string? mode = null, int? limit = null, int? offset = null, Guid[]? repositoryIds = null) => SearchCodeAsync(query, mode, limit, offset, repositoryIds);

    [McpServerTool, Description("Get code slice for indexed file. Path traversal protected. Preview limited to 20 lines.")]
    public async Task<string> GetCodeAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("File path relative to repo root (indexed only)")] string path,
        [Description("Start line 1-based (optional)")] int? startLine = null,
        [Description("End line inclusive (optional)")] int? endLine = null,
        CancellationToken ct = default)
    {
        if (repositoryId == Guid.Empty) return "Error: repositoryId required";
        if (string.IsNullOrWhiteSpace(path)) return "Error: path required";
        if (path.Contains("..")) return "Error: path traversal not allowed";

        _logger.LogInformation("MCP get_code repo={Repo} path='{Path}'", repositoryId, path);
        try
        {
            var slice = await _search.GetCodeAsync(new GetCodeRequest(repositoryId, path, startLine, endLine), ct).ConfigureAwait(false);
            var preview = Truncate(slice.Preview, PreviewLimit);
            var sb = new StringBuilder();
            sb.AppendLine($"File: {slice.Path} language:{slice.Language} lines {slice.StartLine}-{slice.EndLine}/{slice.TotalLines}");
            sb.AppendLine($"Preview ({Math.Min(PreviewLimit, slice.Content.Split('\n').Length)} lines):");
            sb.AppendLine(Indent(preview));
            sb.AppendLine($"Full content length: {slice.Content.Length} chars");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP get_code failed");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_code"), Description("Alias for GetCode")]
    public Task<string> GetCode(Guid repositoryId, string path, int? startLine = null, int? endLine = null) => GetCodeAsync(repositoryId, path, startLine, endLine);

    [McpServerTool, Description("Find symbols by name/FQN substring (LIKE). Returns compact list.")]
    public async Task<string> FindSymbolAsync(
        [Description("Query substring for name/FQN")] string query,
        [Description("Optional repository filter")] Guid? repositoryId = null,
        [Description("Optional language filter")] string? language = null,
        [Description("Limit results (default 20)")] int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query required";
        var req = new FindSymbolRequest(query, repositoryId, language, null, limit ?? 20, 0);
        _logger.LogInformation("MCP find_symbol query='{Query}'", TruncateLog(query));
        var symbols = await _search.FindSymbolAsync(req, ct).ConfigureAwait(false);
        if (symbols.Count == 0) return "No symbols found";
        var sb = new StringBuilder();
        sb.AppendLine($"Found {symbols.Count} symbols:");
        foreach (var s in symbols.Take(limit ?? 20))
        {
            sb.AppendLine($"- {s.Kind} {s.Fqn ?? s.Name} @ {s.FilePath}:{s.StartLine} lang={s.Language} id={s.Id}");
        }
        return TruncateLines(sb.ToString(), 40);
    }

    [McpServerTool(Name = "find_symbol"), Description("Alias for FindSymbol")]
    public Task<string> FindSymbol(string query, Guid? repositoryId = null, string? language = null, int? limit = null) => FindSymbolAsync(query, repositoryId, language, limit);

    [McpServerTool, Description("Find references to symbolId (Relations References -> toSymbolId)")]
    public async Task<string> FindReferencesAsync(Guid symbolId, CancellationToken ct = default)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        _logger.LogInformation("MCP find_references symbol={Id}", symbolId);
        try
        {
            var refs = await _search.FindReferencesAsync(symbolId, ct).ConfigureAwait(false);
            if (refs.Count == 0) return "No references found";
            var sb = new StringBuilder();
            sb.AppendLine($"References ({refs.Count}):");
            foreach (var r in refs.Take(20))
                sb.AppendLine($"- {r.FromSymbolId} -> {r.ToSymbolId} ({r.RelationType})");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_references"), Description("Alias")]
    public Task<string> FindReferences(Guid symbolId) => FindReferencesAsync(symbolId);

    [McpServerTool, Description("Find implementations of interface symbol")]
    public async Task<string> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        _logger.LogInformation("MCP find_implementations symbol={Id}", symbolId);
        try
        {
            var impls = await _search.FindImplementationsAsync(symbolId, ct).ConfigureAwait(false);
            if (impls.Count == 0) return "No implementations found";
            var sb = new StringBuilder();
            sb.AppendLine($"Implementations ({impls.Count}):");
            foreach (var s in impls.Take(20))
                sb.AppendLine($"- {s.Kind} {s.Fqn ?? s.Name} @ {s.FilePath}:{s.StartLine} id={s.Id}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_implementations"), Description("Alias")]
    public Task<string> FindImplementations(Guid symbolId) => FindImplementationsAsync(symbolId);

    [McpServerTool, Description("Find callers (incoming calls) for method symbol")]
    public async Task<string> FindCallersAsync(Guid symbolId, CancellationToken ct = default)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        _logger.LogInformation("MCP find_callers symbol={Id}", symbolId);
        try
        {
            var callers = await _search.FindCallersAsync(symbolId, ct).ConfigureAwait(false);
            if (callers.Count == 0) return "No callers found";
            var sb = new StringBuilder();
            sb.AppendLine($"Callers ({callers.Count}):");
            foreach (var s in callers.Take(20))
                sb.AppendLine($"- {s.Kind} {s.Fqn ?? s.Name} @ {s.FilePath}:{s.StartLine} id={s.Id}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_callers"), Description("Alias")]
    public Task<string> FindCallers(Guid symbolId) => FindCallersAsync(symbolId);

    [McpServerTool, Description("Find callees (outgoing calls) for method symbol")]
    public async Task<string> FindCalleesAsync(Guid symbolId, CancellationToken ct = default)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        _logger.LogInformation("MCP find_callees symbol={Id}", symbolId);
        try
        {
            var callees = await _search.FindCalleesAsync(symbolId, ct).ConfigureAwait(false);
            if (callees.Count == 0) return "No callees found";
            var sb = new StringBuilder();
            sb.AppendLine($"Callees ({callees.Count}):");
            foreach (var s in callees.Take(20))
                sb.AppendLine($"- {s.Kind} {s.Fqn ?? s.Name} @ {s.FilePath}:{s.StartLine} id={s.Id}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_callees"), Description("Alias")]
    public Task<string> FindCallees(Guid symbolId) => FindCalleesAsync(symbolId);

    // Helpers
    private static SearchMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return SearchMode.Hybrid;
        return Enum.TryParse<SearchMode>(mode, true, out var m) ? m : SearchMode.Hybrid;
    }

    private static string Truncate(string s, int maxLines)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var lines = s.Split('\n');
        if (lines.Length <= maxLines) return s;
        return string.Join("\n", lines.Take(maxLines));
    }

    private static string TruncateLines(string s, int maxLines) => Truncate(s, maxLines);

    private static string Indent(string s)
    {
        var lines = s.Split('\n');
        return string.Join("\n", lines.Select(l => "    " + l));
    }

    private static string TruncateLog(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s;
}
