namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Абстракция векторного индекса — IVectorStore из architecture.md §3 и ADR-002.
/// Upsert/Delete/Search с фильтром по репозиториям.
/// </summary>
public interface IVectorStore
{
    Task UpsertAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default);

    Task DeleteByChunkIdsAsync(IEnumerable<Guid> chunkIds, CancellationToken ct = default);

    Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int k, VectorSearchFilter? filter, CancellationToken ct = default);
}
