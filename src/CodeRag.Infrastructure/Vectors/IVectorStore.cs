namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Абстракция векторного индекса — IVectorStore из architecture.md §3 и ADR-002.
/// Upsert/Delete/Search с фильтром по репозиториям.
/// </summary>
public interface IVectorStore
{
    Task UpsertAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default);

    Task DeleteByChunkIdsAsync(IEnumerable<Guid> chunkIds, CancellationToken ct = default);

    /// <summary>Remove all vectors + metadata belonging to a repository.</summary>
    Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>
    /// Drop the index if its vector dimension differs from <paramref name="expectedDim"/>
    /// (e.g. after the embedding model changed). Intended for explicit, user-confirmed rebuilds —
    /// the store never does this silently on its own. Returns true if the index was dropped.
    /// </summary>
    Task<bool> ResetIndexAsync(int expectedDim, CancellationToken ct = default);

    /// <summary>Summary of stored index models/dims for staleness checks (diagnostics).</summary>
    Task<VectorIndexSummary> GetIndexSummaryAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int k, VectorSearchFilter? filter, CancellationToken ct = default);
}
