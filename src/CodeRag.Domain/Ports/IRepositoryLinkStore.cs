using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IRepositoryLinkStore
{
    Task UpsertAsync(RepositoryLink link, CancellationToken ct = default);
    Task<RepositoryLink?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Links where the repository is either the source or the target.</summary>
    Task<IReadOnlyList<RepositoryLink>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>All links.</summary>
    Task<IReadOnlyList<RepositoryLink>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
