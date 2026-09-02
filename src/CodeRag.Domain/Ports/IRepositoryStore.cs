using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

/// <summary>Порт репозитория — объявляется в Domain (DIP), реализуется в Infrastructure.</summary>
public interface IRepositoryStore
{
    Task UpsertAsync(Repository repository, CancellationToken ct = default);
    Task<Repository?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Repository?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task DeleteCascadeAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>Raw stored local_path for a repository (null if the row is missing). Bypasses value-object validation.</summary>
    Task<string?> GetLocalPathAsync(Guid id, CancellationToken ct = default);
}
