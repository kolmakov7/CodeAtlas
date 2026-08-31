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
    private readonly ILogger<CodeSearchTools> _logger;
    private const int MaxOutputLines = 200;

    public CodeSearchTools(ISearchService search, IEmbeddingProviderFactory embeddings, IStatisticsQueryService stats, ILogger<CodeSearchTools> logger)
    {
        _search = search;
        _embeddings = embeddings;
        _stats = stats;
        _logger = logger;
    }

    [McpServerTool, Description("List indexed repositories with status and counts (files, chunks, symbols, relations, vectors per embedding domain). Use to discover repositoryIds for scoping searches.")]
    public async Task<string> RepositoriesAsync(CancellationToken ct = default)
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

    [McpServerTool, Description("Report the two independent embedding domains (code + text): configured model and vector dimensions for each endpoint.")]
    public string EmbeddingStatus()
    {
        _logger.LogInformation("MCP embedding_status");
        var sb = new StringBuilder();
        sb.AppendLine("Embedding domains:");
        foreach (var domain in new[] { EmbeddingDomain.Code, EmbeddingDomain.Text })
        {
            try
            {
                var provider = _embeddings.Get(domain);
                sb.AppendLine($"- {domain.ToText()}: model={provider.Model} dimensions={provider.Dimensions}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- {domain.ToText()}: unavailable ({ex.Message})");
            }
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "embedding_status"), Description("Alias for EmbeddingStatus")]
    public string EmbeddingStatusAlias() => EmbeddingStatus();

    [McpServerTool, Description("Code search (Hybrid by default: semantic + FullText + exact; Semantic = vectors only, no FTS; FullText = lexical only; Symbol = name lookup). Results include repositoryId, chunkId and symbolId so you can follow up with get_code / get_symbol / get_relations. Use to find code by query.")]
    public async Task<string> SearchCodeAsync(
        [Description("Search query text")] string query,
        [Description("Mode: Hybrid|FullText|Semantic|Symbol (default Hybrid)")] string? mode = null,
        [Description("Max results (1..100, default 20)")] int? limit = null,
        [Description("Offset for pagination (default 0)")] int? offset = null,
        [Description("Optional repository filter (Guid array)")] Guid[]? repositoryIds = null,
        [Description("Optional language filter (comma-separated, e.g. 'csharp,python')")] string? languages = null,
        [Description("Optional symbol kind filter (e.g. method, class)")] string? symbolKind = null,
        [Description("Optional minimum semantic score 0..1 (default from server config, typically 0.25)")] double? minScore = null,
        CancellationToken ct = default)
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

    [McpServerTool, Description("Get code slice for indexed file. Path traversal protected. Default 200 lines.")]
    public async Task<string> GetCodeAsync(
        [Description("Repository Id (Guid)")] Guid repositoryId,
        [Description("File path relative to repo root (indexed only)")] string path,
        [Description("Start line 1-based (optional)")] int? startLine = null,
        [Description("End line inclusive (optional)")] int? endLine = null,
        [Description("Max preview lines (default 200, max 1000)")] int? maxLines = null,
        CancellationToken ct = default)
    {
        if (repositoryId == Guid.Empty) return "Error: repositoryId required";
        if (string.IsNullOrWhiteSpace(path)) return "Error: path required";
        if (path.Contains("..")) return "Error: path traversal not allowed";
        var limit = Math.Clamp(maxLines ?? 200, 1, 1000);

        _logger.LogInformation("MCP get_code repo={Repo} path='{Path}'", repositoryId, path);
        try
        {
            var slice = await _search.GetCodeAsync(new GetCodeRequest(repositoryId, path, startLine, endLine), ct).ConfigureAwait(false);
            var preview = Truncate(slice.Preview, limit);
            var sb = new StringBuilder();
            sb.AppendLine($"File: {slice.Path} language:{slice.Language} lines {slice.StartLine}-{slice.EndLine}/{slice.TotalLines}");
            sb.AppendLine($"Preview ({Math.Min(limit, slice.Content.Split('\n').Length)} lines):");
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
    public Task<string> GetCode(Guid repositoryId, string path, int? startLine = null, int? endLine = null, int? maxLines = null) => GetCodeAsync(repositoryId, path, startLine, endLine, maxLines);

    [McpServerTool, Description("Find symbols by name/FQN/namespace substring (LIKE). Returns kind, FQN, location, symbol id, namespace and signature.")]
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
            sb.AppendLine($"  namespace:{s.Namespace} signature:{TruncateLine(s.Signature, 200)}");
        }
        return TruncateLines(sb.ToString(), MaxOutputLines);
    }

    [McpServerTool(Name = "find_symbol"), Description("Alias for FindSymbol")]
    public Task<string> FindSymbol(string query, Guid? repositoryId = null, string? language = null, int? limit = null) => FindSymbolAsync(query, repositoryId, language, limit);

    [McpServerTool, Description("Get full details for one symbol by id: kind, name, FQN, namespace, signature, location, repository, and the containing type/member (Contains relation).")]
    public async Task<string> GetSymbolAsync(Guid symbolId, CancellationToken ct = default)
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

    [McpServerTool, Description("Traverse relations of a symbol. direction: outgoing|incoming|both (default both). relationTypes: optional comma list (Calls, Inherits, Implements, DependsOn, Contains, Overrides). Returns relation type plus both endpoints with FQN, location and id — use the endpoint ids to keep traversing.")]
    public async Task<string> GetRelationsAsync(
        Guid symbolId,
        [Description("Direction: outgoing|incoming|both (default both)")] string? direction = null,
        [Description("Optional comma-separated relation types, e.g. 'Calls,DependsOn'")] string? relationTypes = null,
        [Description("Max relations (default 20, max 200)")] int? limit = null,
        CancellationToken ct = default)
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
    public async Task<string> FindReferencesAsync(Guid symbolId, CancellationToken ct = default)
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
                sb.AppendLine($"- {FormatSymbol(s, s.Id)}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_implementations"), Description("Alias")]
    public Task<string> FindImplementations(Guid symbolId) => FindImplementationsAsync(symbolId);

    [McpServerTool, Description("Find callers (incoming Calls) for a method symbol")]
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
                sb.AppendLine($"- {FormatSymbol(s, s.Id)}");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "find_callers"), Description("Alias")]
    public Task<string> FindCallers(Guid symbolId) => FindCallersAsync(symbolId);

    [McpServerTool, Description("Find callees (outgoing Calls) for a method symbol")]
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
