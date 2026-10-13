namespace CodeRag.Domain.Enums;

/// <summary>
/// The kind of target a documentation entry is generated for.
/// Repository / Module are scope-level targets; Symbol covers class/interface/record/method
/// (the exact granularity is the symbol's own <see cref="SymbolKind"/>).
/// </summary>
public enum DocumentationTargetType
{
    Repository = 0,
    Module = 1,
    Symbol = 2
}

public static class DocumentationTargetTypeExtensions
{
    /// <summary>Normalizes the wire/API representation to an enum (case-insensitive).</summary>
    public static bool TryParseType(string? raw, out DocumentationTargetType type)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && Enum.TryParse<DocumentationTargetType>(raw.Trim(), ignoreCase: true, out var parsed))
        {
            type = parsed;
            return true;
        }

        type = DocumentationTargetType.Symbol;
        return false;
    }

    /// <summary>Canonical lowercase name used in storage and APIs.</summary>
    public static string ToText(this DocumentationTargetType type) => type switch
    {
        DocumentationTargetType.Repository => "repository",
        DocumentationTargetType.Module => "module",
        DocumentationTargetType.Symbol => "symbol",
        _ => "symbol"
    };
}
