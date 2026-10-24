using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace CodeRag.Knowledge.Services;

public sealed class KnowledgeSearchService
{
    private readonly IKnowledgeStore _knowledge;
    private readonly IEmbeddingProviderFactory _providers;
    private readonly IVectorStoreFactory _vectors;
    private readonly ILogger<KnowledgeSearchService> _logger;

    public KnowledgeSearchService(
        IKnowledgeStore knowledge,
        IEmbeddingProviderFactory providers,
        IVectorStoreFactory vectors,
        ILogger<KnowledgeSearchService> logger)
    {
        _knowledge = knowledge;
        _providers = providers;
        _vectors = vectors;
        _logger = logger;
    }

    public async Task<KnowledgeSearchResult> SearchAsync(
        KnowledgeSearchRequest request, CancellationToken ct = default)
    {
        var mode = (request.Mode ?? "hybrid").Trim().ToLowerInvariant();
        var query = request.Query?.Trim() ?? "";
        var filter = ToQuery(request);
        var warnings = new List<string>();

        if (query.Length == 0)
        {
            var listed = await _knowledge.ListAsync(filter, ct).ConfigureAwait(false);
            return new KnowledgeSearchResult(
                listed.Select(e => new KnowledgeSearchHit(e, 0, "list")).ToList(), warnings, "list");
        }

        if (mode is "lexical" or "fulltext" or "full-text")
        {
            var lexical = await _knowledge.SearchLexicalAsync(query, filter, ct).ConfigureAwait(false);
            return new KnowledgeSearchResult(
                lexical.Select((e, i) => new KnowledgeSearchHit(e, 1.0 / (i + 1), "lexical")).ToList(),
                warnings, "lexical");
        }

        if (mode == "semantic")
        {
            var semantic = await SemanticAsync(query, request, ct).ConfigureAwait(false);
            warnings.AddRange(semantic.Warnings);
            return new KnowledgeSearchResult(
                semantic.Hits.Select(h => new KnowledgeSearchHit(h.Entry, h.Score, "semantic")).ToList(),
                warnings, "semantic");
        }

        var lexicalTask = _knowledge.SearchLexicalAsync(query, filter, ct);
        var semanticTask = SemanticAsync(query, request, ct);
        await Task.WhenAll(lexicalTask, semanticTask).ConfigureAwait(false);
        var lexicalResults = await lexicalTask.ConfigureAwait(false);
        var semanticResults = await semanticTask.ConfigureAwait(false);
        warnings.AddRange(semanticResults.Warnings);

        var combined = new Dictionary<Guid, (KnowledgeEntry Entry, double Score, HashSet<string> Sources)>();
        for (var i = 0; i < lexicalResults.Count; i++)
        {
            var entry = lexicalResults[i];
            combined[entry.Id] = (entry, 1.0 / (60 + i + 1), new HashSet<string> { "lexical" });
        }
        for (var i = 0; i < semanticResults.Hits.Count; i++)
        {
            var hit = semanticResults.Hits[i];
            var rrf = 1.0 / (60 + i + 1);
            if (combined.TryGetValue(hit.Entry.Id, out var existing))
            {
                existing.Sources.Add("semantic");
                combined[hit.Entry.Id] = (existing.Entry, existing.Score + rrf, existing.Sources);
            }
            else
            {
                combined[hit.Entry.Id] = (hit.Entry, rrf, new HashSet<string> { "semantic" });
            }
        }

        var hits = combined.Values
            .OrderByDescending(x => x.Score)
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 200))
            .Select(x => new KnowledgeSearchHit(x.Entry, x.Score, string.Join("+", x.Sources.OrderBy(s => s))))
            .ToList();
        return new KnowledgeSearchResult(hits, warnings, "hybrid");
    }

    private async Task<(IReadOnlyList<KnowledgeSearchHit> Hits, IReadOnlyList<string> Warnings)> SemanticAsync(
        string query, KnowledgeSearchRequest request, CancellationToken ct)
    {
        var warnings = new List<string>();
        try
        {
            var provider = _providers.Text;
            var vectors = await provider.EmbedAsync(new[] { query }, ct).ConfigureAwait(false);
            if (vectors.Length == 0) return (Array.Empty<KnowledgeSearchHit>(), new[] { "embeddings returned no vector" });

            var k = Math.Clamp(request.Limit * 5 + request.Offset, 20, 500);
            var scored = await _vectors.Knowledge.SearchAsync(vectors[0], k, null, ct).ConfigureAwait(false);
            if (scored.Count == 0) return (Array.Empty<KnowledgeSearchHit>(), warnings);

            var ids = scored.Select(s => s.ChunkId).ToList();
            var entries = (await _knowledge.GetByIdsAsync(ids, ct).ConfigureAwait(false))
                .ToDictionary(e => e.Id);
            var hits = new List<KnowledgeSearchHit>();
            foreach (var item in scored)
            {
                if (!entries.TryGetValue(item.ChunkId, out var entry)) continue;
                if (!Matches(entry, request)) continue;
                hits.Add(new KnowledgeSearchHit(entry, item.Score, "semantic"));
            }
            return (hits, warnings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic knowledge search unavailable");
            warnings.Add("semantic search unavailable: " + ex.Message);
            return (Array.Empty<KnowledgeSearchHit>(), warnings);
        }
    }

    private static bool Matches(KnowledgeEntry entry, KnowledgeSearchRequest request)
    {
        if (!request.IncludeArchived && entry.Status is KnowledgeStatus.Archived) return false;
        if (request.GlobalOnly) return entry.ProjectId is null;
        if (request.ProjectId is not null && entry.ProjectId != request.ProjectId) return false;
        if (request.RepositoryId is not null && entry.RepositoryId != request.RepositoryId) return false;
        if (request.Types is { Count: > 0 } && !request.Types.Contains(entry.Type)) return false;
        if (request.Statuses is { Count: > 0 } && !request.Statuses.Contains(entry.Status)) return false;
        return true;
    }

    private static KnowledgeQuery ToQuery(KnowledgeSearchRequest request) => new(
        Query: request.Query,
        Types: request.Types,
        Statuses: request.Statuses,
        ProjectId: request.ProjectId,
        RepositoryId: request.RepositoryId,
        GlobalOnly: request.GlobalOnly,
        IncludeArchived: request.IncludeArchived,
        Limit: request.Limit,
        Offset: request.Offset);
}
