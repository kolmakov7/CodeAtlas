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
        if (!IsValidHost(host.Trim())) throw new DomainValidationException($"Proxy Host '{host}' is not a valid host. Must be a DNS name (e.g. proxy.example.com) or IP address, optionally with http:// or https:// prefix.");

        var cleanHost = ExtractHost(host.Trim());

        return new ProxyConfig(id, name.Trim(), scheme, cleanHost, port)
        {
            Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim(),
            PasswordEncrypted = string.IsNullOrWhiteSpace(passwordEncrypted) ? null : passwordEncrypted
        };
    }

    public static ProxyConfig Rehydrate(Guid id, string name, ProxyScheme scheme, string host, int port, string? username, string? passwordEncrypted)
    {
        // No validation for hydration (allows legacy invalid hosts to be listed)
        return new ProxyConfig(id, name, scheme, host, port)
        {
            Username = username,
            PasswordEncrypted = passwordEncrypted
        };
    }

    private static bool IsValidHost(string host)
    {
        var h = host.Trim();
        // Allow http:// or https:// prefix — extract host part
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(h, UriKind.Absolute, out var uri)) return false;
            h = uri.Host;
        }
        else if (h.Contains("://"))
        {
            return false;
        }
        h = h.Trim();
        if (string.IsNullOrWhiteSpace(h)) return false;
        // Try IP
        if (System.Net.IPAddress.TryParse(h, out _)) return true;
        // DNS hostname: RFC 1123
        // Labels separated by dots, each label 1-63 chars, alphanum + hyphen, not start/end with hyphen
        var labels = h.Split('.');
        if (labels.Length == 0) return false;
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (!System.Text.RegularExpressions.Regex.IsMatch(label, @"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")) return false;
        }
        // Must contain at least one dot or be single label with letters? Allow single label like "proxy" for internal?
        // But require at least one letter
        if (h.Length > 253) return false;
        return true;
    }

    private static string ExtractHost(string host)
    {
        var h = host.Trim();
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(h, UriKind.Absolute, out var uri)) return uri.Host;
        }
        return h;
    }
}
