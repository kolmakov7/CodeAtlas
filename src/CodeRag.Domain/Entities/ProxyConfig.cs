using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Прокси для доступа к Git remote (таблица proxies).</summary>
public sealed class ProxyConfig
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public ProxyScheme Scheme { get; private set; }
    public string Host { get; private set; }
    public int Port { get; private set; }
    public string? Username { get; private set; }
    public string? PasswordEncrypted { get; private set; }

    private ProxyConfig(Guid id, string name, ProxyScheme scheme, string host, int port)
    {
        Id = id;
        Name = name;
        Scheme = scheme;
        Host = host;
        Port = port;
    }

    public static ProxyConfig Create(
        Guid id, string name, ProxyScheme scheme, string host, int port,
        string? username = null, string? passwordEncrypted = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Proxy Id must not be empty.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Proxy Name must not be empty.");
        if (string.IsNullOrWhiteSpace(host)) throw new DomainValidationException("Proxy Host must not be empty.");
        if (port is <= 0 or > 65535) throw new DomainValidationException("Proxy Port must be 1..65535.");

        return new ProxyConfig(id, name.Trim(), scheme, host.Trim(), port)
        {
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            PasswordEncrypted = string.IsNullOrWhiteSpace(passwordEncrypted) ? null : passwordEncrypted
        };
    }
}
