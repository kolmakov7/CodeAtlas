using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IFileStore
{
    Task UpsertAsync(FileEntry file, CancellationToken ct = default);
    Task UpsertBatchAsync(IEnumerable<FileEntry> files, CancellationToken ct = default);
    Task<FileEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<FileEntry?> GetByPathAsync(Guid repositoryId, string path, CancellationToken ct = default);
    Task<IReadOnlyList<FileEntry>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
}
