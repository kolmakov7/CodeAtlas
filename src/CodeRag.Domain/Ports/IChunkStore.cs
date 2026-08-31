using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IChunkStore
{
    Task UpsertAsync(Chunk chunk, CancellationToken ct = default);
    Task UpsertBatchAsync(IEnumerable<Chunk> chunks, CancellationToken ct = default);
    Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetByFileAsync(Guid fileId, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetBySymbolAsync(Guid symbolId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteByFileAsync(Guid fileId, CancellationToken ct = default);
    Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
}
