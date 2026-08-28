namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Вход для векторного индекса. Соответствует IVectorStore.UpsertAsync контракта из architecture.md.
/// </summary>
public sealed record VectorEntry(
    Guid ChunkId,
    Guid RepositoryId,
    float[] Embedding,
    string Model = "default",
    int Dim = 384
);

/// <summary>
/// Результат векторного поиска.
/// </summary>
public sealed record ScoredChunk(
    Guid ChunkId,
    double Score,
    double Distance
);

/// <summary>
/// Фильтр для векторного поиска (часть Filter из architecture.md).
/// </summary>
public sealed record VectorSearchFilter(
    IReadOnlyList<Guid>? RepositoryIds = null,
    IReadOnlyList<string>? Languages = null
)
{
    public static VectorSearchFilter Empty => new();
}
