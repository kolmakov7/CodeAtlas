using System.ComponentModel;
using System.Text;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Search;
using CodeRag.Search.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSearchTools : IMcpToolHandler
{
    private readonly ISearchService _search;
    private readonly IEmbeddingProviderFactory _embeddings;
    private readonly IStatisticsQueryService _stats;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<CodeSearchTools> _logger;
    private const int MaxOutputLines = 200;

    public CodeSearchTools(ISearchService search, IEmbeddingProviderFactory embeddings, IStatisticsQueryService stats, ILogger<CodeSearchTools> logger, IMcpUsageRecorder? usage = null)
    {
        _search = search;
        _embeddings = embeddings;
        _stats = stats;
        _logger = logger;
        _usage = usage;
    }

    /// <summary>
    /// Runs a tool and records one usage row (tool, ok/error, duration, optional repo scope).
    /// Never affects the tool result: exceptions are converted to the standard "Error: ..." text.
    /// </summary>
    private async Task<string> TrackAsync(string tool, Guid? repositoryId, Func<Task<string>> execute)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try
        {
            result = await execute().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = $"Error: {ex.Message}";
        }
        sw.Stop();
        _usage?.Record(tool, !result.StartsWith("Error:", StringComparison.Ordinal), (int)sw.ElapsedMilliseconds, repositoryId);
        return result;
    }

    [McpServerTool, Description("List indexed repositories with status and counts (files, chunks, symbols, relations, vectors per embedding domain). Use to discover repositoryIds for scoping searches.")]
    public Task<string> RepositoriesAsync(CancellationToken ct = default) => TrackAsync("repositories", null, () => RepositoriesCoreAsync(ct));

    private async Task<string> RepositoriesCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("MCP repositories");
        try
        {
            var g = await _stats.GetGlobalStatisticsAsync(ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"Repositories: {g.RepositoryCount} (totals: files={g.FileCount} chunks={g.ChunkCount} symbols={g.SymbolCount} relations={g.RelationCount})");
            foreach (var r in g.Repositories)
            {
                var embed = string.Join(", ", r.EmbeddingStats.Select(e => $"{e.Domain} {e.Indexed}/{e.Pending}"));
                sb.AppendLine($"- {r.Name} id={r.RepositoryId} status={r.Status} files={r.FileCount} chunks={r.ChunkCount} symbols={r.SymbolCount} relations={r.RelationCount} vectors={r.VectorCount} embeddings({embed})");
            }
            return TruncateLines(sb.ToString(), MaxOutputLines);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "repositories"), Description("Alias for Repositories")]
    public Task<string> Repositories() => RepositoriesAsync();

    [McpServerTool, Description("Report the two independent embedding domains (code + text): status, configured model, vector dimensions and indexed/pending vector counts per domain. Use to verify which embedding endpoints the index was built against.")]
    public Task<string> EmbeddingStatus(CancellationToken ct = default) => TrackAsync("embedding_status", null, () => EmbeddingStatusCoreAsync(ct));

    private async Task<string> EmbeddingStatusCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("MCP embedding_status");
        IReadOnlyList<CodeRag.Domain.Ports.EmbeddingDomainStats>? global = null;
        try
        {
            global = (await _stats.GetGlobalStatisticsAsync(ct).ConfigureAwait(false)).EmbeddingStats;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP embedding_status: statistics unavailable");
        }

        var sb = new StringBuilder();
        sb.AppendLine("Embedding domains:");
        foreach (var domain in new[] { EmbeddingDomain.Code, EmbeddingDomain.Text })
        {
            var text = domain.ToText();
            try
            {
                var provider = _embeddings.Get(domain);
                var stats = global?.FirstOrDefault(e => string.Equals(e.Domain, text, StringComparison.OrdinalIgnoreCase));
                var status = stats is { Indexed: > 0 } ? "ready" : "configured, no vectors";
                sb.AppendLine($"- {text}: status={status} model={provider.Model} dimensions={provider.Dimensions} vectors={stats?.Indexed ?? 0} pending={stats?.Pending ?? 0}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- {text}: status=unavailable model=? dimensions=? ({ex.Message})");
            }
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "embedding_status"), Description("Alias for EmbeddingStatus")]
    public Task<string> EmbeddingStatusAlias(CancellationToken ct = default) => EmbeddingStatus(ct);

    [McpServerTool, Description("Search indexed code and documentation. Hybrid is the default and combines semantic, FullText, exact and structural signals. Use SemanticCode for natural-language source-code intent, SemanticText for documentation/prose, FullText for exact identifiers, and Symbol for an exact symbol name/FQN. Semantic merges code and text vectors and is retained for compatibility; prefer the explicit domain modes. Results include repositoryId, file, lines, chunkId and symbolId for follow-up with get_code, get_symbol or get_relations. Scope with repositoryIds, languages or symbolKind; use minScore to remove weak semantic matches.")]
    public Task<string> SearchCodeAsync(
        [Description("Search query text")] string query,
        [Description("Search mode: Hybrid (default, best first try), SemanticCode (source intent), SemanticText (docs/prose), FullText (exact tokens), Symbol (exact name/FQN), or Semantic (merged code+text compatibility mode)")] string? mode = null,
        [Description("Max results (1..100, default 20)")] int? limit = null,
        [Description("Offset for pagination (default 0)")] int? offset = null,
        [Description("Optional repository filter (Guid array)")] Guid[]? repositoryIds = null,
        [Description("Optional language filter (comma-separated, e.g. 'csharp,python')")] string? languages = null,
        [Description("Optional symbol kind filter (e.g. method, class)")] string? symbolKind = null,
        [Description("Optional minimum semantic score 0..1 (default from server config, typically 0.25)")] double? minScore = null,
        CancellationToken ct = default)
        => TrackAsync("search_code", repositoryIds?.Length == 1 ? repositoryIds[0] : null,
            () => SearchCodeCoreAsync(query, mode, limit, offset, repositoryIds, languages, symbolKind, minScore, ct));

    private async Task<string> SearchCodeCoreAsync(
        string query, string? mode, int? limit, int? offset, Guid[]? repositoryIds,
        string? languages, string? symbolKind, double? minScore, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query must not be empty";
        var m = ParseMode(mode);
        var langArray = ParseCommaList(languages);
        var req = new SearchRequest(query, repositoryIds, langArray, symbolKind, m, limit ?? 20, offset ?? 0, minScore);
        _logger.LogInformation("MCP search_code query='{Query}' mode={Mode} limit={Limit}", TruncateLog(query), m, limit);
        var result = await _search.SearchCodeAsync(req, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"Total candidates: {result.Total}, returned: {result.Items.Count}, elapsedMs: {result.ElapsedMs}");
        if (result.Warnings.Count > 0) sb.AppendLine($"Warnings: {string.Join(", ", result.Warnings)}");
        foreach (var item in result.Items)
        {
            var preview = Truncate(item.Preview, 20);
            sb.AppendLine($"- repo:{item.RepositoryName ?? item.RepositoryId.ToString()} repoId:{item.RepositoryId} file:{item.FilePath} lines:{item.StartLine}-{item.EndLine} score:{item.Score:F3} origin:{item.Origin} chunkId:{item.ChunkId}");
            if (!string.IsNullOrEmpty(item.SymbolName)) sb.AppendLine($"  symbol: {item.SymbolName} ({item.Fqn}) symbolId:{item.SymbolId}");
            sb.AppendLine($"  preview:\n{Indent(preview)}");
        }
        return TruncateLines(sb.ToString(), MaxOutputLines);
    }

    // MCP alias: search_code -> SearchCode
    [McpServerTool(Name = "search_code"), Description("Alias for SearchCode")]
    public Task<string> SearchCode(string query, string? mode = null, int? limit = null, int? offset = null, Guid[]? repositoryIds = null, double? minScore = null) => SearchCodeAsync(query, mode, limit, offset, repositoryIds, minScore: minScore);

    [McpServerTool, Description("Get source code of an indexed file (exact lines). Path traversal protected. Default 200 lines; when truncated the output says which line to continue from.")]
    public Task<string> GetCodeAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("File path relative to repo root (indexed only)")] string path,
        [Description("Start line 1-based (optional)")] int? startLine = null,
        [Description("End line inclusive (optional)")] int? endLine = null,
        [Description("Max lines to return (default 200, max 1000)")] int? maxLines = null,
        CancellationToken ct = default)
        => TrackAsync("get_code", repositoryId,
            () => GetCodeCoreAsync(repositoryId, path, startLine, endLine, maxLines, ct));

    private async Task<string> GetCodeCoreAsync(
        Guid repositoryId, string path, int? startLine, int? endLine, int? maxLines, CancellationToken ct)
    {
        if (repositoryId == Guid.Empty) return "Error: repositoryId required";
        if (string.IsNullOrWhiteSpace(path)) return "Error: path required";
        if (path.Contains("..")) return "Error: path traversal not allowed";
        var limit = Math.Clamp(maxLines ?? 200, 1, 1000);

        _logger.LogInformation("MCP get_code repo={Repo} path='{Path}'", repositoryId, path);
        try
        {
            var slice = await _search.GetCodeAsync(new GetCodeRequest(repositoryId, path, startLine, endLine, limit), ct).ConfigureAwait(false);
            var requestedEnd = endLine ?? slice.TotalLines;
            var truncated = slice.EndLine < requestedEnd;
            var sb = new StringBuilder();
            sb.AppendLine($"File: {slice.Path} language:{slice.Language} lines {slice.StartLine}-{slice.EndLine}/{slice.TotalLines}");
            sb.AppendLine(Indent(Truncate(slice.Content, limit)));
            if (truncated)
                sb.AppendLine($"[truncated at maxLines={limit}; continue with startLine={slice.EndLine + 1}]");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP get_code failed");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_code"), Description("Alias for GetCode")]
    public Task<string> GetCode(Guid repositoryId, string path, int? startLine = null, int? endLine = null, int? maxLines = null) => GetCodeAsync(repositoryId, path, startLine, endLine, maxLines);

    [McpServerTool, Description("Find symbols by name/FQN/namespace substring (LIKE). Returns kind, FQN, location, symbol id, namespace and signature.")]
    public Task<string> FindSymbolAsync(
        [Description("Query substring for name/FQN")] string query,
        [Description("Optional repository filter")] Guid? repositoryId = null,
        [Description("Optional language filter")] string? language = null,
        [Description("Limit results (default 20)")] int? limit = null,
        CancellationToken ct = default)
        => TrackAsync("find_symbol", repositoryId,
            () => FindSymbolCoreAsync(query, repositoryId, language, limit, ct));

    private async Task<string> FindSymbolCoreAsync(
        string query, Guid? repositoryId, string? language, int? limit, CancellationToken ct)
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
            sb.AppendLine($"  namespace:{s.Namespace} signature:{TruncateLine(s.Signature, 200)}");
        }
        return TruncateLines(sb.ToString(), MaxOutputLines);
    }

    [McpServerTool(Name = "find_symbol"), Description("Alias for FindSymbol")]
    public Task<string> FindSymbol(string query, Guid? repositoryId = null, string? language = null, int? limit = null) => FindSymbolAsync(query, repositoryId, language, limit);

    [McpServerTool, Description("Get full details for one symbol by id: kind, name, FQN, namespace, signature, location, repository, and the containing type/member (Contains relation).")]
    public Task<string> GetSymbolAsync(Guid symbolId, CancellationToken ct = default)
        => TrackAsync("get_symbol", null, () => GetSymbolCoreAsync(symbolId, ct));

    private async Task<string> GetSymbolCoreAsync(Guid symbolId, CancellationToken ct)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        _logger.LogInformation("MCP get_symbol symbol={Id}", symbolId);
        try
        {
            var s = await _search.GetSymbolAsync(symbolId, ct).ConfigureAwait(false);
            if (s is null) return "Symbol not found";
            var sb = new StringBuilder();
            sb.AppendLine($"Symbol: {s.Kind} {s.Fqn ?? s.Name} id={s.Id}");
            sb.AppendLine($"  name:{s.Name} namespace:{s.Namespace} language:{s.Language}");
            sb.AppendLine($"  signature:{TruncateLine(s.Signature, 400)}");
            sb.AppendLine($"  location:{s.FilePath}:{s.StartLine}-{s.EndLine} repositoryId:{s.RepositoryId}");
            var container = await GetContainerAsync(symbolId, ct).ConfigureAwait(false);
            if (container is not null)
                sb.AppendLine($"  contained in: {container.Kind} {container.Fqn ?? container.Name} id={container.Id}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "get_symbol"), Description("Alias for GetSymbol")]
    public Task<string> GetSymbol(Guid symbolId) => GetSymbolAsync(symbolId);

    private async Task<Symbol?> GetContainerAsync(Guid symbolId, CancellationToken ct)
    {
        var incoming = await _search.GetRelationsAsync(symbolId, RelationDirection.Incoming, new[] { "Contains" }, ct).ConfigureAwait(false);
        if (incoming.Count == 0) return null;
        return incoming[0].FromSymbol;
    }

    [McpServerTool, Description("Traverse relations of a symbol. direction: outgoing|incoming|both (default both). relationTypes: optional comma list; case-insensitive with aliases — 'calls', 'contains', 'depends_on', 'inherits', 'implements', 'overrides', 'refs' (references), 'uses', 'imports'. Returns relation type plus both endpoints with FQN, location and id — use the endpoint ids to keep traversing.")]
    public Task<string> GetRelationsAsync(
        Guid symbolId,
        [Description("Direction: outgoing|incoming|both (default both)")] string? direction = null,
        [Description("Optional comma-separated relation types, e.g. 'calls,depends_on'. Case-insensitive; aliases like 'refs' accepted.")] string? relationTypes = null,
        [Description("Max relations (default 20, max 200)")] int? limit = null,
        CancellationToken ct = default)
        => TrackAsync("get_relations", null,
            () => GetRelationsCoreAsync(symbolId, direction, relationTypes, limit, ct));

    private async Task<string> GetRelationsCoreAsync(
        Guid symbolId, string? direction, string? relationTypes, int? limit, CancellationToken ct)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        var dir = ParseDirection(direction);
        var types = ParseCommaList(relationTypes);
        var max = Math.Clamp(limit ?? 20, 1, 200);
        _logger.LogInformation("MCP get_relations symbol={Id} direction={Dir} types={Types}", symbolId, dir, string.Join(",", types ?? Array.Empty<string>()));
        try
        {
            var all = await _search.GetRelationsAsync(symbolId, dir, types, ct).ConfigureAwait(false);
            if (all.Count == 0) return "No relations found";
            var sb = new StringBuilder();
            sb.AppendLine($"Relations for {symbolId}: total={all.Count}, showing={Math.Min(max, all.Count)} direction={dir}");
            foreach (var r in all.Take(max))
            {
                var from = FormatSymbol(r.FromSymbol, r.FromSymbolId);
                var to = FormatSymbol(r.ToSymbol, r.ToSymbolId);
                sb.AppendLine($"- [{r.RelationType}] {from} -> {to}");
            }
            return TruncateLines(sb.ToString(), MaxOutputLines);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "get_relations"), Description("Alias for GetRelations")]
    public Task<string> GetRelations(Guid symbolId, string? direction = null, string? relationTypes = null, int? limit = null) => GetRelationsAsync(symbolId, direction, relationTypes, limit);

    [McpServerTool, Description("Find references/usages of a symbol (incoming Calls/DependsOn/References-style relations). Returns hydrated endpoints, not raw ids.")]
    public Task<string> FindReferencesAsync(Guid symbolId, CancellationToken ct = default)
        => TrackAsync("find_references", null, () => FindReferencesCoreAsync(symbolId, ct));

    private async Task<string> FindReferencesCoreAsync(Guid symbolId, CancellationToken ct)
    {
        if (symbolId == Guid.Empty) return "Error: symbolId required";
        _logger.LogInformation("MCP find_references symbol={Id}", symbolId);
        try
        {
            var refs = await _search.FindReferencesAsync(symbolId, ct).ConfigureAwait(false);
            if (refs.Count == 0) return "No references found";
            var sb = new StringBuilder();
            sb.AppendLine($"References ({refs.Count}, showing up to 20):");
            foreach (var r in refs.Take(20))
            {
                var from = await _search.GetSymbolAsync(r.FromSymbolId, ct).ConfigureAwait(false);
                sb.AppendLine($"- {r.RelationType}: {FormatSymbol(from, r.FromSymbolId)} -> {r.ToSymbolId}");
            }
            return TruncateLines(sb.ToString(), MaxOutputLines);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_references"), Description("Alias")]
    public Task<string> FindReferences(Guid symbolId) => FindReferencesAsync(symbolId);

    [McpServerTool, Description("Find implementations of an interface symbol (incoming Implements)")]
    public Task<string> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default)
        => TrackAsync("find_implementations", null, () => FindImplementationsCoreAsync(symbolId, ct));

    private async Task<string> FindImplementationsCoreAsync(Guid symbolId, CancellationToken ct)
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
                sb.AppendLine($"- {FormatSymbol(s, s.Id)}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_implementations"), Description("Alias")]
    public Task<string> FindImplementations(Guid symbolId) => FindImplementationsAsync(symbolId);

    [McpServerTool, Description("Find callers (incoming Calls) for a method symbol")]
    public Task<string> FindCallersAsync(Guid symbolId, CancellationToken ct = default)
        => TrackAsync("find_callers", null, () => FindCallersCoreAsync(symbolId, ct));

    private async Task<string> FindCallersCoreAsync(Guid symbolId, CancellationToken ct)
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
                sb.AppendLine($"- {FormatSymbol(s, s.Id)}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_callers"), Description("Alias")]
    public Task<string> FindCallers(Guid symbolId) => FindCallersAsync(symbolId);

    [McpServerTool, Description("Find callees (outgoing Calls) for a method symbol")]
    public Task<string> FindCalleesAsync(Guid symbolId, CancellationToken ct = default)
        => TrackAsync("find_callees", null, () => FindCalleesCoreAsync(symbolId, ct));

    private async Task<string> FindCalleesCoreAsync(Guid symbolId, CancellationToken ct)
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
                sb.AppendLine($"- {FormatSymbol(s, s.Id)}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_callees"), Description("Alias")]
    public Task<string> FindCallees(Guid symbolId) => FindCalleesAsync(symbolId);

    // Helpers
    private static RelationDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction)) return RelationDirection.Both;
        return direction.Trim().ToLowerInvariant() switch
        {
            "outgoing" or "out" => RelationDirection.Outgoing,
            "incoming" or "in" => RelationDirection.Incoming,
            _ => RelationDirection.Both
        };
    }

    private static string[]? ParseCommaList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : null;
    }

    private static string FormatSymbol(Symbol? s, Guid id)
    {
        if (s is null) return id.ToString();
        return $"{s.Kind} {s.Fqn ?? s.Name} @ {s.FilePath}:{s.StartLine} id={s.Id}";
    }

    private static string TruncateLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace("\n", " ").Replace("\r", " ");
        return s.Length > max ? s.Substring(0, max) + "..." : s;
    }

    private static SearchMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return SearchMode.Hybrid;
        var n = mode.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        return n switch
        {
            "hybrid" => SearchMode.Hybrid,
            "fulltext" or "fts" or "lexical" => SearchMode.FullText,
            "semantic" => SearchMode.Semantic,
            "semanticcode" or "code" or "codesemantic" => SearchMode.SemanticCode,
            "semantictext" or "text" or "textsemantic" or "docs" => SearchMode.SemanticText,
            "symbol" => SearchMode.Symbol,
            _ => Enum.TryParse<SearchMode>(mode, true, out var m) ? m : SearchMode.Hybrid
        };
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
