using CodeRag.Domain.Enums;

namespace CodeRag.Documentation.Models;

/// <summary>
/// A request to document one target: a whole repository, a directory module, or a single symbol
/// (class/interface/record/method). Carries just enough identity to build the context and to
/// key the persisted entry, without duplicating index data.
/// </summary>
public sealed record DocumentationTarget(
    DocumentationTargetType Type,
    Guid RepositoryId,
    string TargetRef,
    string Name,
    Guid? SymbolId = null,
    string? RepositoryName = null,
    string? Namespace = null,
    string? FilePath = null,
    string? Language = null,
    string? IndexedCommit = null,
    string? Branch = null)
{
    public bool IsSymbol => Type == DocumentationTargetType.Symbol;

    /// <summary>Stable identity key used by the store and the staleness check.</summary>
    public string TargetKey => TargetRef;

    /// <summary>
    /// Canonical target ref for a target type, so callers (API/MCP/service) agree on identity:
    /// repository → "repository"; module → normalized directory path; symbol → FQN (or name).
    /// </summary>
    public static string BuildTargetRef(DocumentationTargetType type, string? fqn, string? name, string? path, string? module) => type switch
    {
        DocumentationTargetType.Repository => "repository",
        DocumentationTargetType.Module => NormalizePath(module ?? path),
        DocumentationTargetType.Symbol => string.IsNullOrWhiteSpace(fqn) ? (name ?? "symbol") : fqn,
        _ => string.IsNullOrWhiteSpace(fqn) ? (name ?? "symbol") : fqn
    };

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "root";
        var p = path.Trim().Replace('\\', '/').Trim('/');
        // Drop a leading "./" (or ".") segment so "  ./src  " normalizes to "src".
        while (p == "." || p.StartsWith("./", StringComparison.Ordinal))
            p = (p == "." ? string.Empty : p[2..]).Trim('/');
        return string.IsNullOrEmpty(p) ? "root" : p;
    }
}
