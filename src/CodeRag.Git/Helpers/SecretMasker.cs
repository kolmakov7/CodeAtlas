using System.Text.RegularExpressions;

namespace CodeRag.Git.Helpers;

/// <summary>Маскирование секретов для логов и LastError. Никогда не логировать proxy URL с паролем.</summary>
public static class SecretMasker
{
    private static readonly Regex CredentialInUrl = new(@"(://[^:]+:)([^@]+)(@)", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"(token|password|passwd|secret)\s*[:=]\s*\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BearerPattern = new(@"Bearer\s+[A-Za-z0-9\-_\.=]+", RegexOptions.Compiled);
    private static readonly Regex SocksUrlPattern = new(@"(socks5h?://[^:]+:)([^@]+)(@)", RegexOptions.Compiled);

    public static string Mask(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var s = input;

        // Mask proxy URL user:pass@
        s = CredentialInUrl.Replace(s, "$1***$3");
        s = SocksUrlPattern.Replace(s, "$1***$3");
        s = BearerPattern.Replace(s, "Bearer ***");
        s = TokenPattern.Replace(s, m =>
        {
            var idx = m.Value.IndexOf(':');
            if (idx < 0) idx = m.Value.IndexOf('=');
            if (idx >= 0) return m.Value.Substring(0, idx + 1) + " ***";
            return "***";
        });

        // Mask -c credential.helper helper password token inline
        // Example: echo "password=secretToken"
        s = Regex.Replace(s, @"password\s*=\s*\S+", "password=***", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"username\s*=\s*\S+", "username=***", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"http\.proxy\s*=\s*\S+", "http.proxy=***");
        s = Regex.Replace(s, @"https\.proxy\s*=\s*\S+", "https.proxy=***");

        return s;
    }

    public static string MaskProxyUrl(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return string.Empty;
        return Mask(proxyUrl);
    }

    public static string MaskException(Exception ex)
    {
        return Mask(ex.ToString());
    }
}
