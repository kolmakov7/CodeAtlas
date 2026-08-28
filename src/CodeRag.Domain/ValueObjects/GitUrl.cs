using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.ValueObjects;

/// <summary>
/// Value-object для Git URL. Допускает https/http/ssh/git схемы и scp-синтаксис git@host:path.
/// Инвариант: непустой, соответствует одному из разрешённых паттернов.
/// </summary>
public sealed record GitUrl
{
    public string Value { get; }

    private GitUrl(string value) => Value = value;

    public static GitUrl Create(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new DomainValidationException("GitUrl must not be empty.");

        var trimmed = url.Trim();

        if (IsValid(trimmed))
            return new GitUrl(trimmed);

        throw new DomainValidationException(
            $"Invalid GitUrl '{trimmed}'. Expected https://, http://, ssh://, git:// or git@host:path / user@host:path.");
    }

    public static bool IsValid(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var s = url.Trim();

        // https://, http://, ssh://, git://
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(s, UriKind.Absolute, out _);
        }

        // scp-like: [user@]host:path  (e.g. git@github.com:org/repo.git)
        // Must contain ':' and not contain '://', host part must contain '.'
        if (!s.Contains("://") && s.Contains(':') && s.Contains('@'))
        {
            var atIdx = s.IndexOf('@');
            var colonIdx = s.IndexOf(':', atIdx);
            if (colonIdx > atIdx + 1 && s.Length > colonIdx + 1)
            {
                var host = s.Substring(atIdx + 1, colonIdx - atIdx - 1);
                if (host.Contains('.') && !host.Contains('/') && !host.Contains(' '))
                    return true;
            }
        }

        return false;
    }

    public override string ToString() => Value;

    public static implicit operator string(GitUrl g) => g.Value;
}
