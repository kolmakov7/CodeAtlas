using CodeRag.Domain.Entities;

namespace CodeRag.Git.Stores;

public interface IProxyStore
{
    Task UpsertAsync(ProxyConfig proxy, CancellationToken ct = default);
    Task<ProxyConfig?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ProxyConfig>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface ICredentialStore
{
    Task UpsertAsync(Credential credential, CancellationToken ct = default);
    Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
