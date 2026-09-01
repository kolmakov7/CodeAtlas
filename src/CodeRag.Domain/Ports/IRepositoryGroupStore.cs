using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IRepositoryGroupStore
{
    Task UpsertAsync(RepositoryGroup group, CancellationToken ct = default);
    Task<RepositoryGroup?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<RepositoryGroup?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryGroup>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryGroup>> GetByProxyIdAsync(Guid proxyId, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryGroup>> GetByCredentialIdAsync(Guid credentialId, CancellationToken ct = default);
    Task<IReadOnlyList<Entities.Repository>> GetRepositoriesByGroupAsync(Guid groupId, CancellationToken ct = default);
}
