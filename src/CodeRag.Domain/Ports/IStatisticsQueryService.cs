namespace CodeRag.Domain.Ports;

public sealed record RepositoryStatistics(
    Guid RepositoryId,
    string Name,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int VectorCount,
    string? IndexedCommit,
    string? CurrentCommit,
    string Status);

public sealed record GlobalStatistics(
    int RepositoryCount,
    int FileCount,
    int SymbolCount,
    int ChunkCount,
    int JobCount,
    IReadOnlyList<RepositoryStatistics> Repositories);

public sealed record StorageSizes(long SqliteBytes, long VectorBytes, long ReposBytes);

public interface IStatisticsQueryService
{
    Task<RepositoryStatistics> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default);
    Task<GlobalStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default);
    Task<StorageSizes> GetStorageSizesAsync(CancellationToken ct = default);
}
