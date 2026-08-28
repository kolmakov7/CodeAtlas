using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.ValueObjects;

/// <summary>
/// Value-object для локального пути репозитория.
/// Инвариант: абсолютный путь внутри /data/repos (WAL/volume — единственный).
/// Нормализует путь и отклоняет traversal за пределы корня.
/// </summary>
public sealed record LocalPath
{
    public const string ReposRoot = "/data/repos";

    public string Value { get; }

    private LocalPath(string value) => Value = value;

    public static LocalPath Create(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new DomainValidationException("LocalPath must not be empty.");

        var trimmed = path.Trim();
        Validate(trimmed);
        var normalized = Normalize(trimmed);
        return new LocalPath(normalized);
    }

    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { Validate(path.Trim()); return true; }
        catch (DomainValidationException) { return false; }
    }

    private static void Validate(string path)
    {
        // Must be absolute
        if (!path.StartsWith('/'))
            throw new DomainValidationException($"LocalPath must be absolute and inside {ReposRoot}: '{path}'.");

        var normalized = Normalize(path);

        // Must be inside ReposRoot (allow exact root? spec says inside /data/repos — require prefix)
        // Allow /data/repos itself? spec says inside — but permit equality for edge, plus child.
        if (normalized != ReposRoot && !normalized.StartsWith(ReposRoot + "/", StringComparison.Ordinal))
            throw new DomainValidationException($"LocalPath must be inside {ReposRoot}: '{path}' -> '{normalized}'.");

        if (normalized.Contains("//"))
            throw new DomainValidationException($"LocalPath contains empty segment: '{path}'.");
    }

    private static string Normalize(string path)
    {
        // Use simple normalization: resolve . and .. without touching filesystem.
        // Split by '/', process stack.
        var parts = path.Split('/', StringSplitOptions.None);
        var stack = new Stack<string>();

        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p) || p == ".") continue;
            if (p == "..")
            {
                if (stack.Count > 0) stack.Pop();
                else
                {
                    // traversal above root -> will fail IsValid after; keep as indicator
                    // push marker to make path escape detection
                    stack.Push("..");
                }
            }
            else
            {
                stack.Push(p);
            }
        }

        var reversed = stack.Reverse().ToArray();
        var result = "/" + string.Join("/", reversed);

        // preserve trailing slash removal except root
        if (result.Length > 1 && path.EndsWith('/') && !result.EndsWith('/'))
        {
            // normalized without trailing slash is canonical
        }

        // handle case where original was /data/repos and normalization kept it
        if (result == "/") result = "/";
        return result;
    }

    public override string ToString() => Value;

    public static implicit operator string(LocalPath p) => p.Value;
}
