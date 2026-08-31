namespace CodeRag.Domain.Ports;

/// <summary>
/// Per-domain (code / text) embedding progress for a repository or the whole index.
/// Indexed — chunks of this domain that already have a stored vector; Pending — the rest.
/// </summary>
public sealed record EmbeddingDomainStats(string Domain, int Indexed, int Pending);

public sealed record RepositoryStatistics(
    Guid RepositoryId,
    string Name,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int VectorCount,
    IReadOnlyList<EmbeddingDomainStats> EmbeddingStats,
    int EmbeddingsFailed,
    string? IndexedCommit,
    string? CurrentCommit,
    string Status);

public sealed record GlobalStatistics(
    int RepositoryCount,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int JobCount,
    IReadOnlyList<EmbeddingDomainStats> EmbeddingStats,
    int EmbeddingsFailed,
    IReadOnlyList<RepositoryStatistics> Repositories);

public sealed record StorageSizes(long SqliteBytes, long VectorBytes, long ReposBytes);

public interface IStatisticsQueryService
{
    Task<RepositoryStatistics> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default);
    Task<GlobalStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default);
    Task<StorageSizes> GetStorageSizesAsync(CancellationToken ct = default);
}
