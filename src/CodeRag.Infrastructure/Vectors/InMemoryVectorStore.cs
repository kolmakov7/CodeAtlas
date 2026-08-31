using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// InMemory fallback для IVectorStore когда sqlite-vec недоступен.
/// Хранит эмбеддинги в памяти процесса, поддержка фильтра по repository.
/// Search — brute-force cosine similarity.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<Guid, VectorEntry> _store = new();
    private readonly ILogger<InMemoryVectorStore> _logger;

    public InMemoryVectorStore(ILogger<InMemoryVectorStore> logger, bool logWarning = true)
    {
        _logger = logger;
        if (logWarning)
            _logger.LogWarning("Using InMemoryVectorStore fallback — sqlite-vec not available. Vectors stored only in memory; restart will lose data.");
        else
            _logger.LogDebug("InMemoryVectorStore created as fallback helper (sqlite-vec may be available)");
    }

    public Task UpsertAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default)
    {
        foreach (var e in entries)
        {
            _store[e.ChunkId] = e;
        }
        _logger.LogDebug("InMemoryVectorStore upserted {Count} entries, total {Total}", entries.Count(), _store.Count);
        return Task.CompletedTask;
    }

    public Task DeleteByChunkIdsAsync(IEnumerable<Guid> chunkIds, CancellationToken ct = default)
    {
        foreach (var id in chunkIds)
        {
            _store.TryRemove(id, out _);
        }
        return Task.CompletedTask;
    }

    public Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        foreach (var kv in _store.Where(x => x.Value.RepositoryId == repositoryId).ToList())
            _store.TryRemove(kv.Key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ResetIndexAsync(int expectedDim, CancellationToken ct = default)
    {
        var stale = _store.Values.Any(v => v.Embedding.Length != expectedDim);
        if (stale)
        {
            _store.Clear();
            _logger.LogInformation("InMemoryVectorStore cleared: stored dim differs from expected {Dim}", expectedDim);
        }
        return Task.FromResult(stale);
    }

    public Task<VectorIndexSummary> GetIndexSummaryAsync(CancellationToken ct = default)
    {
        var stored = _store.Values
            .GroupBy(v => (v.Model, v.Dim, v.ContextLength))
            .Select(g => new VectorIndexModelInfo(g.Key.Model, g.Key.Dim, g.Key.ContextLength, g.Count()))
            .OrderBy(x => x.Model)
            .ThenBy(x => x.Dim)
            .ToList();
        return Task.FromResult(new VectorIndexSummary(stored));
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int k, VectorSearchFilter? filter, CancellationToken ct = default)
    {
        if (queryVector is null || queryVector.Length == 0)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        var queryNorm = Normalize(queryVector);

        var candidates = _store.Values.AsEnumerable();

        if (filter?.RepositoryIds is { Count: > 0 } repoIds)
        {
            var set = new HashSet<Guid>(repoIds);
            candidates = candidates.Where(v => set.Contains(v.RepositoryId));
        }

        var scored = new List<ScoredChunk>();
        foreach (var entry in candidates)
        {
            // cosine similarity: dot(normQuery, normEntry)
            var embNorm = Normalize(entry.Embedding);
            double dot = 0;
            int len = Math.Min(queryNorm.Length, embNorm.Length);
            for (int i = 0; i < len; i++) dot += queryNorm[i] * embNorm[i];
            // distance = 1 - cosine
            double distance = 1.0 - dot;
            double score = dot; // higher is better
            scored.Add(new ScoredChunk(entry.ChunkId, score, distance));
        }

        var top = scored.OrderBy(s => s.Distance).Take(k).ToList();
        return Task.FromResult<IReadOnlyList<ScoredChunk>>(top);
    }

    private static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = Math.Sqrt(sum);
        if (norm < 1e-9) return v;
        var res = new float[v.Length];
        for (int i = 0; i < v.Length; i++) res[i] = (float)(v[i] / norm);
        return res;
    }

    // Exposed for diagnostics
    public int Count => _store.Count;

    public void Clear() => _store.Clear();

    public bool HasDimensionMismatch(int dim) => _store.Values.Any(v => v.Embedding.Length != dim);
}
