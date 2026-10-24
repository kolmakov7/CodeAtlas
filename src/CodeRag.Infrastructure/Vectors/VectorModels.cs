using CodeRag.Domain.Enums;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Источник размерности по умолчанию (используется когда IEmbeddingProvider ещё не зарегистрирован).
/// </summary>
internal sealed class NullDimSource : CodeRag.Domain.Ports.IVectorDimSource
{
    public int Dimensions => 0;
    public int? ContextLength => null;
}

/// <summary>
/// Вход для векторного индекса. Соответствует IVectorStore.UpsertAsync контракта из architecture.md.
/// </summary>
public sealed record VectorEntry(
    Guid ChunkId,
    Guid RepositoryId,
    float[] Embedding,
    string Model = "default",
    int Dim = 384,
    int? ContextLength = null,
    string OwnerType = "chunk"
);

/// <summary>Одна модель, по которой хранятся векторы (счётчик чанков).</summary>
public sealed record VectorIndexModelInfo(string Model, int Dim, int? ContextLength, int ChunkCount);

/// <summary>Сводка по содержимому векторного индекса — для проверки устаревания (stale index).</summary>
public sealed record VectorIndexSummary(IReadOnlyList<VectorIndexModelInfo> StoredModels)
{
    public static VectorIndexSummary Empty { get; } = new(Array.Empty<VectorIndexModelInfo>());
}

/// <summary>
/// Результат векторного поиска.
/// </summary>
public sealed record ScoredChunk(
    Guid ChunkId,
    double Score,
    double Distance,
    EmbeddingDomain Domain = EmbeddingDomain.Code
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
