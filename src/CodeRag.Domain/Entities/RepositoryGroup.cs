using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>
/// Группа репозиториев. Каждая группа имеет один Proxy и один Credentials.
/// Репозиторий принадлежит одной группе и получает Proxy/Credentials через неё.
/// </summary>
public sealed class RepositoryGroup
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public Guid? ProxyId { get; private set; }
    public Guid? CredentialId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private RepositoryGroup(Guid id, string name, Guid? proxyId, Guid? credentialId, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        ProxyId = proxyId;
        CredentialId = credentialId;
        CreatedAt = createdAt;
    }

    public static RepositoryGroup Create(Guid id, string name, Guid? proxyId = null, Guid? credentialId = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("RepositoryGroup Id must not be empty.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("RepositoryGroup Name must not be empty.");
        return new RepositoryGroup(id, name.Trim(), proxyId, credentialId, DateTimeOffset.UtcNow);
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Name must not be empty.");
        Name = name.Trim();
    }

    public void SetProxy(Guid? proxyId) => ProxyId = proxyId;
    public void SetCredential(Guid? credentialId) => CredentialId = credentialId;

    internal static RepositoryGroup Rehydrate(Guid id, string name, Guid? proxyId, Guid? credentialId, DateTimeOffset createdAt)
    {
        return new RepositoryGroup(id, name, proxyId, credentialId, createdAt);
    }
}
