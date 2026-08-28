using CodeRag.Domain.Exceptions;
using CodeRag.Domain.ValueObjects;

namespace CodeRag.Git.Helpers;

/// <summary>Валидация LocalPath внутри ReposRoot.</summary>
public static class PathValidator
{
    public const string DefaultRoot = "/data/repos";

    public static string ValidateAndNormalize(string localPath, string reposRoot = DefaultRoot)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new DomainValidationException("LocalPath must not be empty.");
        if (string.IsNullOrWhiteSpace(reposRoot))
            reposRoot = DefaultRoot;

        // Use domain value object for primary validation
        var lp = LocalPath.Create(localPath);
        var normalized = lp.Value;

        var root = reposRoot.TrimEnd('/');
        if (normalized != root && !normalized.StartsWith(root + "/", StringComparison.Ordinal))
            throw new DomainValidationException($"LocalPath must be inside {root}: '{localPath}'.");

        // Additional check: no traversal, already normalized by LocalPath
        if (normalized.Contains(".."))
            throw new DomainValidationException($"LocalPath traversal not allowed: '{localPath}'.");

        return normalized;
    }

    public static void EnsureInside(string localPath, string reposRoot = DefaultRoot)
    {
        ValidateAndNormalize(localPath, reposRoot);
    }
}
