using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Enums;

namespace CodeRag.Indexer;

/// <summary>
/// Отделяет "кодинг" от "текста" для выбора embedding-домена (code vs text).
/// Правила (по постановке):
///  - исходный код (csharp/python/sql/vbs/cpp/javascript/go, включая комментарии) → <see cref="EmbeddingDomain.Code"/>;
///  - markdown / README / docs / проза → <see cref="EmbeddingDomain.Text"/>;
///  - неизвестные файлы по умолчанию уходят в code (CodeAtlas — это code search).
/// </summary>
public static class ContentClassifier
{
    /// <summary>Расширения, которые почти всегда проза/документация.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx", ".rst", ".adoc",
    };

    public static EmbeddingDomain Classify(string filePath, string? language)
    {
        var normalized = (filePath ?? string.Empty).Replace('\\', '/');
        var lowerName = Path.GetFileName(normalized).ToLowerInvariant();
        var lowerPath = normalized.ToLowerInvariant();
        var ext = Path.GetExtension(normalized);

        // Чистая документация по расширению.
        if (TextExtensions.Contains(ext))
            return EmbeddingDomain.Text;

        // README / changelog / license и т.п. — проза независимо от расширения.
        if (lowerName.StartsWith("readme", StringComparison.Ordinal)
            || lowerName.StartsWith("changelog", StringComparison.Ordinal)
            || lowerName.StartsWith("contributing", StringComparison.Ordinal)
            || lowerName.StartsWith("license", StringComparison.Ordinal)
            || lowerName.StartsWith("authors", StringComparison.Ordinal))
            return EmbeddingDomain.Text;

        // Всё в папке docs/documentation, что не является исходным кодом.
        if ((lowerPath.Contains("/docs/", StringComparison.Ordinal) || lowerPath.Contains("/documentation/", StringComparison.Ordinal))
            && (language == null || !SupportedLanguages.All.Contains(language)))
            return EmbeddingDomain.Text;

        // Всё остальное — код (включая все поддерживаемые языки).
        return EmbeddingDomain.Code;
    }
}
