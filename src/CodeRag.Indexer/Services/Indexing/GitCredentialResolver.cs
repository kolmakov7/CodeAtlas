using CodeRag.Domain.Entities;

namespace CodeRag.Indexer.Services.Indexing;

internal sealed class GitCredentialResolver
{
    private readonly CodeRag.Domain.Ports.IRepositoryGroupStore _groupStore;
    private readonly CodeRag.Git.Stores.IProxyStore _proxyStore;
    private readonly CodeRag.Git.Stores.ICredentialStore _credStore;

    public GitCredentialResolver(
        CodeRag.Domain.Ports.IRepositoryGroupStore groupStore,
        CodeRag.Git.Stores.IProxyStore proxyStore,
        CodeRag.Git.Stores.ICredentialStore credStore)
    {
        _groupStore = groupStore;
        _proxyStore = proxyStore;
        _credStore = credStore;
    }

    public async Task<(ProxyConfig? Proxy, Credential? Credential)> ResolveAsync(Repository repo, CancellationToken ct)
    {
        if (!repo.GroupId.HasValue) return (null, null);
        var group = await _groupStore.GetByIdAsync(repo.GroupId.Value, ct).ConfigureAwait(false);
        if (group is null) return (null, null);
        ProxyConfig? proxy = group.ProxyId.HasValue ? await _proxyStore.GetByIdAsync(group.ProxyId.Value, ct).ConfigureAwait(false) : null;
        Credential? cred = group.CredentialId.HasValue ? await _credStore.GetByIdAsync(group.CredentialId.Value, ct).ConfigureAwait(false) : null;
        return (proxy, cred);
    }
}
