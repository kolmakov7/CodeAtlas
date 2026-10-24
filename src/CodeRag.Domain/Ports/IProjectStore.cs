using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IProjectStore
{
    Task UpsertAsync(Project project, CancellationToken ct = default);
    Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Project>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
    Task<Project?> GetDefaultForRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
    Task SetRepositoriesAsync(Guid projectId, IEnumerable<Guid> repositoryIds, CancellationToken ct = default);
}
