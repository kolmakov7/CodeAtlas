using CodeRag.Domain.Entities;

namespace CodeRag.Infrastructure.Stores;

/// <summary>
/// Хранилище репозиториев — CRUD поверх SQLite (Dapper).
/// </summary>
public interface IRepositoryStore
{
    Task UpsertAsync(Repository repository, CancellationToken ct = default);
    Task<Repository?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Repository?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>
    /// Каскадное удаление репозитория (storage.md §5): очищает vector_entries, chunks_fts, каскад FK.
    /// </summary>
    Task DeleteCascadeAsync(Guid repositoryId, CancellationToken ct = default);
}
