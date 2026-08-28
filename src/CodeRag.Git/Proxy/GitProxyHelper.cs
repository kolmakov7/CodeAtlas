using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Git.Auth;

namespace CodeRag.Git.Proxy;

/// <summary>Помощь для построения proxy URL и git -c настроек по ProxyConfig.</summary>
public static class GitProxyHelper
{
    public static string? BuildProxyUrl(ProxyConfig? proxy, ICredentialProtector protector)
    {
        if (proxy is null) return null;

        var scheme = MapScheme(proxy.Scheme);
        var host = proxy.Host;
        var port = proxy.Port;

        string? userInfo = null;
        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            var user = Uri.EscapeDataString(proxy.Username!);
            var pass = string.Empty;
            if (!string.IsNullOrWhiteSpace(proxy.PasswordEncrypted))
            {
                var plain = protector.Unprotect(proxy.PasswordEncrypted!);
                pass = Uri.EscapeDataString(plain);
            }
            userInfo = string.IsNullOrEmpty(pass) ? user : $"{user}:{pass}";
        }
        else if (!string.IsNullOrWhiteSpace(proxy.PasswordEncrypted))
        {
            // edge: password without user (rare) — ignore for URL
            var plain = protector.Unprotect(proxy.PasswordEncrypted!);
            _ = plain;
        }

        var authority = userInfo is null ? $"{host}:{port}" : $"{userInfo}@{host}:{port}";
        return $"{scheme}://{authority}";
    }

    public static string MapScheme(ProxyScheme scheme) => scheme switch
    {
        ProxyScheme.Http => "http",
        ProxyScheme.Https => "https",
        ProxyScheme.Socks5 => "socks5",
        ProxyScheme.Socks5h => "socks5h",
        _ => "http"
    };

    /// <summary>Возвращает -c аргументы для git: -c http.proxy=... -c https.proxy=...</summary>
    public static IReadOnlyList<string> BuildGitConfigArgs(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return Array.Empty<string>();
        // Для SOCKS5 используем socks5h://user:pass@host:port via http.proxy (git умеет)
        // Также устанавливаем https.proxy аналогично.
        return new[]
        {
            "-c", $"http.proxy={proxyUrl}",
            "-c", $"https.proxy={proxyUrl}",
            "-c", $"http.proxyAuthMethod=basic"
        };
    }

    /// <summary>Env vars для proxy: http_proxy, https_proxy, HTTP_PROXY, HTTPS_PROXY, ALL_PROXY, NO_PROXY.</summary>
    public static Dictionary<string, string> BuildEnv(string? proxyUrl, string? noProxy = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(proxyUrl)) return env;

        env["http_proxy"] = proxyUrl;
        env["https_proxy"] = proxyUrl;
        env["HTTP_PROXY"] = proxyUrl;
        env["HTTPS_PROXY"] = proxyUrl;
        env["ALL_PROXY"] = proxyUrl;
        if (!string.IsNullOrWhiteSpace(noProxy))
            env["NO_PROXY"] = noProxy!;
        return env;
    }
}
