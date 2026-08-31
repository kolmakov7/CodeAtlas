namespace CodeRag.Domain.Enums;

/// <summary>
/// Which embedding model/space a chunk and its vector live in.
/// <see cref="Code"/> uses the code embedding model (source code, comments stay code).
/// <see cref="Text"/> uses the text embedding model (Markdown, README, docs, prose).
/// </summary>
public enum EmbeddingDomain
{
    Code = 0,
    Text = 1,
}

public static class EmbeddingDomainExtensions
{
    /// <summary>Stable lowercase token used for DB storage and config sections ("code"/"text").</summary>
    public static string ToText(this EmbeddingDomain domain) => domain switch
    {
        EmbeddingDomain.Text => "text",
        _ => "code",
    };

    /// <summary>Parse "code"/"text" (case-insensitive). Unknown/legacy values fall back to <see cref="EmbeddingDomain.Code"/>.</summary>
    public static EmbeddingDomain ToDomain(this string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return EmbeddingDomain.Code;
        return value.Trim().ToLowerInvariant() switch
        {
            "text" or "prose" or "documentation" or "doc" or "docs" => EmbeddingDomain.Text,
            _ => EmbeddingDomain.Code,
        };
    }
}
