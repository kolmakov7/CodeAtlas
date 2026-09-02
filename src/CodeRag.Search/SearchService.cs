using System.Diagnostics;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Stores;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Search.Models;
using CodeRag.Search.Services;
using CodeRag.Search.Strategies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeRag.Search;

public sealed class SearchService : ISearchService
{
    private readonly ChunkLoader _chunkLoader;
    private readonly SearchRanker _ranker;
    private readonly IEnumerable<ISearchBranch> _branches;
    private readonly SymbolService _symbols;
    private readonly RelationService _relations;
    private readonly FileContentService _files;
    private readonly IOptionsMonitor<SearchOptions> _optionsMonitor;
    private readonly ILogger<SearchService> _logger;
    private SearchOptions Opts => _optionsMonitor.CurrentValue;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SearchService(
        ChunkLoader chunkLoader,
        SearchRanker ranker,
        IEnumerable<ISearchBranch> branches,
        SymbolService symbols,
        RelationService relations,
        FileContentService files,
        IOptionsMonitor<SearchOptions> options,
        ILogger<SearchService> logger)
    {
        _chunkLoader = chunkLoader;
        _ranker = ranker;
        _branches = branches;
        _symbols = symbols;
        _relations = relations;
        _files = files;
        _optionsMonitor = options;
        _logger = logger;
    }

    public static string? NormalizeRelationType(string? token) => RelationService.Normalize(token);

    // Test helper: creates SearchService with same old 6-param signature but via new strategies
    public static SearchService CreateForTests(
        IDbConnectionFactory factory,
        FtsHelper fts,
        IVectorStoreFactory vectorFactory,
        IEmbeddingProviderFactory embeddingFactory,
        ILogger<SearchService> logger,
        IOptionsMonitor<SearchOptions> options)
    {
        var chunkStore = new ChunkStore(factory);
        var symbolStore = new SymbolStore(factory);
        var relationStore = new RelationStore(factory);
        var fileStore = new FileStore(factory);
        var repoStore = new RepositoryStore(factory);

        return new SearchService(
            new ChunkLoader(chunkStore),
            new SearchRanker(options),
            new ISearchBranch[]
            {
                new ExactSearchBranch(symbolStore, chunkStore, options, NullLogger<ExactSearchBranch>.Instance),
                new FtsSearchBranch(fts, NullLogger<FtsSearchBranch>.Instance),
                new VectorSearchBranch(vectorFactory, embeddingFactory, options, NullLogger<VectorSearchBranch>.Instance),
                new StructuralSearchBranch(symbolStore, chunkStore, relationStore, NullLogger<StructuralSearchBranch>.Instance)
            },
            new SymbolService(symbolStore),
            new RelationService(relationStore, new SymbolService(symbolStore)),
            new FileContentService(repoStore, fileStore, chunkStore, options),
            options,
            logger);
    }

    public async Task<SearchResult> SearchCodeAsync(SearchRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var query = req.Query?.Trim() ?? string.Empty;
        var warnings = new List<string>();
        var limit = req.Limit <= 0 ? Opts.DefaultResultCount : Math.Min(req.Limit, Opts.MaxResultCount);
        var offset = Math.Max(0, req.Offset);

        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogInformation("Search query empty mode={Mode} limit={Limit} offset={Offset}", req.Mode, limit, offset);
            return new SearchResult(Array.Empty<SearchResultItem>(), 0, Array.Empty<string>()) { ElapsedMs = sw.ElapsedMilliseconds };
        }

        var repoFilter = req.RepositoryIds?.Length > 0 ? req.RepositoryIds : null;
        var langFilter = req.Languages?.Length > 0 ? req.Languages.Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToArray() : null;
        var symbolKindFilter = string.IsNullOrWhiteSpace(req.SymbolKind) ? null : req.SymbolKind.Trim();

        var candidates = new Dictionary<Guid, CandidateAccumulator>();
        var ctx = new SearchBranchContext(req, query, repoFilter, langFilter, symbolKindFilter, warnings);

        // Order matters: exact must run before structural (structural uses exactSymbolIds)
        var ordered = _branches.OrderBy(b => b.Origin);
        var counts = new Dictionary<SearchOrigin, int>();
        foreach (var branch in ordered)
        {
            if (!branch.ShouldRun(req.Mode)) continue;
            var c = await branch.ContributeAsync(ctx, candidates, ct).ConfigureAwait(false);
            counts[branch.Origin] = c;
        }
        counts.TryGetValue(SearchOrigin.Exact, out var exactCount);
        counts.TryGetValue(SearchOrigin.Fts, out var ftsCount);
        counts.TryGetValue(SearchOrigin.Vector, out var vectorCount);
        counts.TryGetValue(SearchOrigin.Structural, out var structCount);

        if (candidates.Count == 0)
        {
            sw.Stop();
            _logger.LogInformation("Search query={Query} mode={Mode} latency={Latency}ms candidates=0 exact={Exact} fts={Fts} vector={Vec} struct={Struct} total=0 weights lex={Lex} sem={Sem} struct={StructW} warnings={Warnings}", query, req.Mode, sw.ElapsedMilliseconds, exactCount, ftsCount, vectorCount, structCount, Opts.LexicalWeight, Opts.SemanticWeight, Opts.StructuralWeight, warnings.Count == 0 ? "none" : string.Join(",", warnings));
            return new SearchResult(Array.Empty<SearchResultItem>(), 0, warnings) { ElapsedMs = sw.ElapsedMilliseconds };
        }

        var chunkInfos = await _chunkLoader.LoadAsync(candidates.Keys.ToList(), ct).ConfigureAwait(false);

        var filtered = new List<(CandidateAccumulator acc, ChunkInfo info)>();
        foreach (var kv in candidates)
        {
            if (!chunkInfos.TryGetValue(kv.Key, out var info)) continue;
            if (repoFilter != null && repoFilter.Length > 0 && !repoFilter.Contains(info.RepositoryId)) continue;
            if (langFilter != null && langFilter.Length > 0)
            {
                var lang = info.Language ?? info.FileLanguage ?? string.Empty;
                if (!langFilter.Any(l => string.Equals(l, lang, StringComparison.OrdinalIgnoreCase))) continue;
            }
            if (symbolKindFilter != null)
            {
                var kind = info.SymbolKind ?? string.Empty;
                if (!string.Equals(kind, symbolKindFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }
            filtered.Add((kv.Value, info));
        }

        if (filtered.Count == 0)
        {
            sw.Stop();
            _logger.LogInformation("Search query={Query} mode={Mode} latency={Latency}ms candidates={Cand} filtered=0 weights lex={Lex} sem={Sem} struct={StructW}", query, req.Mode, sw.ElapsedMilliseconds, candidates.Count, Opts.LexicalWeight, Opts.SemanticWeight, Opts.StructuralWeight);
            return new SearchResult(Array.Empty<SearchResultItem>(), 0, warnings) { ElapsedMs = sw.ElapsedMilliseconds };
        }

        var ranked = _ranker.Rank(filtered, req.Mode);
        var orderedRanked = ranked.OrderByDescending(x => x.finalScore).ThenBy(x => x.info.RepositoryId).ThenBy(x => x.info.Language ?? x.info.FileLanguage ?? string.Empty, StringComparer.Ordinal).ThenBy(x => x.info.FilePath, StringComparer.Ordinal).ThenBy(x => x.acc.ChunkId).ToList();

        var total = orderedRanked.Count;
        var paged = orderedRanked.Skip(offset).Take(limit).ToList();

        var items = new List<SearchResultItem>(paged.Count);
        foreach (var (acc, info, score) in paged)
        {
            var preview = BuildPreview(info.Source, Opts.PreviewMaxLines);
            var origin = string.Join(",", acc.Origins.Select(o => o.ToString().ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));
            var startLine = info.StartLine ?? 1;
            var endLine = info.EndLine ?? startLine;
            items.Add(new SearchResultItem(info.RepositoryId, info.FileId, info.SymbolId, info.Language ?? info.FileLanguage ?? info.SymbolLanguage ?? "unknown", info.FilePath, startLine, endLine, score, preview, origin) { ChunkId = acc.ChunkId, SymbolName = info.SymbolName, Fqn = info.Fqn, RepositoryName = info.RepositoryName });
        }

        sw.Stop();
        _logger.LogInformation("Search query={Query} mode={Mode} latency={Latency}ms candidates={Cand} total={Total} returned={Returned} exact={Exact} fts={Fts} vector={Vec} struct={Struct} weights lex={Lex} sem={Sem} structW={StructW} warnings={Warnings}", query, req.Mode, sw.ElapsedMilliseconds, candidates.Count, total, items.Count, exactCount, ftsCount, vectorCount, structCount, Opts.LexicalWeight, Opts.SemanticWeight, Opts.StructuralWeight, warnings.Count == 0 ? "none" : string.Join(",", warnings));
        return new SearchResult(items, total, warnings) { ElapsedMs = sw.ElapsedMilliseconds };
    }

    public Task<IReadOnlyList<Symbol>> FindSymbolAsync(FindSymbolRequest req, CancellationToken ct = default) => _symbols.FindAsync(req, ct);
    public Task<Symbol?> GetSymbolAsync(Guid symbolId, CancellationToken ct = default) => _symbols.GetAsync(symbolId, ct);
    public Task<IReadOnlyList<Reference>> FindReferencesAsync(Guid symbolId, CancellationToken ct = default) => _relations.FindReferencesAsync(symbolId, ct);
    public Task<IReadOnlyList<RelationEntry>> GetRelationsAsync(Guid symbolId, CodeRag.Domain.Enums.RelationDirection direction = CodeRag.Domain.Enums.RelationDirection.Both, string[]? types = null, CancellationToken ct = default) => _relations.GetRelationsAsync(symbolId, direction, types, ct);
    public Task<IReadOnlyList<Symbol>> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default) => _relations.FindImplementationsAsync(symbolId, ct);
    public Task<IReadOnlyList<Symbol>> FindCallersAsync(Guid symbolId, CancellationToken ct = default) => _relations.FindCallersAsync(symbolId, ct);
    public Task<IReadOnlyList<Symbol>> FindCalleesAsync(Guid symbolId, CancellationToken ct = default) => _relations.FindCalleesAsync(symbolId, ct);
    public Task<SourceSlice> GetCodeAsync(GetCodeRequest req, CancellationToken ct = default) => _files.GetAsync(req, ct);

    private static string BuildPreview(string source, int maxLines)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        var lines = source.Split('\n');
        if (lines.Length > maxLines) lines = lines.Take(maxLines).ToArray();
        for (int i = 0; i < lines.Length; i++) if (lines[i].EndsWith("\r")) lines[i] = lines[i][..^1];
        var preview = string.Join("\n", lines);
        if (preview.Length > 4000) preview = preview[..4000] + "\n...";
        return preview;
    }
}
