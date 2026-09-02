using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Search.Models;
using Microsoft.Extensions.Logging;

namespace CodeRag.Search.Strategies;

public sealed class VectorSearchBranch : ISearchBranch
{
    public SearchOrigin Origin => SearchOrigin.Vector;
    private readonly IVectorStoreFactory _vectorFactory;
    private readonly IEmbeddingProviderFactory _embeddingFactory;
    private readonly SearchOptions _opts;
    private readonly ILogger<VectorSearchBranch> _logger;

    public VectorSearchBranch(IVectorStoreFactory vectorFactory, IEmbeddingProviderFactory embeddingFactory, Microsoft.Extensions.Options.IOptionsMonitor<SearchOptions> opts, ILogger<VectorSearchBranch> logger)
    {
        _vectorFactory = vectorFactory;
        _embeddingFactory = embeddingFactory;
        _opts = opts.CurrentValue;
        _logger = logger;
    }

    public bool ShouldRun(SearchMode mode) => mode == SearchMode.Hybrid || mode == SearchMode.Semantic || mode == SearchMode.SemanticCode || mode == SearchMode.SemanticText;

    public async Task<int> ContributeAsync(SearchBranchContext ctx, Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct)
    {
        var vecResults = await GetVectorCandidatesAsync(ctx.Query, ctx.RepoFilter, ctx.Warnings, ctx.Mode, ct).ConfigureAwait(false);
        var minScore = ctx.Request.MinScore ?? _opts.MinSemanticScore;
        var textFloor = Math.Max(minScore, _opts.MinTextSemanticScore);
        var relevantVec = vecResults.Where(r => r.Score >= (r.Domain == EmbeddingDomain.Text ? textFloor : minScore)).ToList();
        foreach (var r in relevantVec)
        {
            if (!candidates.TryGetValue(r.ChunkId, out var acc))
            {
                acc = new CandidateAccumulator(r.ChunkId);
                candidates[r.ChunkId] = acc;
            }
            acc.Origins.Add(Origin);
            if (!acc.VectorScore.HasValue || r.Score > acc.VectorScore.Value)
            {
                acc.VectorScore = r.Score;
                acc.VectorDistance = r.Distance;
                acc.VectorDomain = r.Domain;
            }
        }
        return relevantVec.Count;
    }

    private async Task<IReadOnlyList<ScoredChunk>> GetVectorCandidatesAsync(string query, Guid[]? repoIds, List<string> warnings, SearchMode mode, CancellationToken ct)
    {
        var merged = new List<ScoredChunk>();
        var filter = (repoIds != null && repoIds.Length > 0) ? new VectorSearchFilter(repoIds) : VectorSearchFilter.Empty;
        var anySucceeded = false;
        var domains = mode switch
        {
            SearchMode.SemanticCode => new[] { EmbeddingDomain.Code },
            SearchMode.SemanticText => new[] { EmbeddingDomain.Text },
            _ => new[] { EmbeddingDomain.Code, EmbeddingDomain.Text }
        };
        foreach (var domain in domains)
        {
            IEmbeddingProvider provider;
            IVectorStore store;
            try
            {
                provider = _embeddingFactory.Get(domain);
                store = _vectorFactory.Get(domain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector branch skipped for {Domain}: factory unavailable query={Query}", domain.ToText(), query);
                continue;
            }
            float[] queryVector;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                var vectors = await provider.EmbedAsync(new[] { query }, timeoutCts.Token).ConfigureAwait(false);
                if (vectors.Length == 0 || vectors[0] is null || vectors[0].Length == 0)
                    throw new InvalidOperationException("Empty embedding result");
                queryVector = vectors[0];
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Vector branch timeout for {Domain} query={Query}", domain.ToText(), query);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector branch embeddings failed for {Domain} query={Query}", domain.ToText(), query);
                continue;
            }
            IReadOnlyList<ScoredChunk> results;
            try { results = await store.SearchAsync(queryVector, _opts.VectorTopK, filter, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector store search failed for {Domain} query={Query}", domain.ToText(), query);
                continue;
            }
            if (results.Count == 0) continue;
            anySucceeded = true;
            foreach (var r in results)
                merged.Add(new ScoredChunk(r.ChunkId, (double)Math.Clamp(r.Score, 0.0, 1.0), r.Distance, domain));
        }
        if (!anySucceeded)
        {
            warnings.Add("embeddings unavailable");
            _logger.LogWarning("Vector branch skipped: no domain produced candidates (embeddings unavailable) query={Query}", query);
        }
        return merged;
    }
}
