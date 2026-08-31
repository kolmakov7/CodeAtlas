using CodeRag.Api.Models;
using CodeRag.Search.Models;

namespace CodeRag.Api.Mappers;

/// <summary>
/// Маппер Search домен → API DTO. Вынесен из контроллера для SRP/тестируемости.
/// Централизует fallback RepositoryName и усечение Preview (20 строк).
/// </summary>
public static class SearchResultMapper
{
    private const int PreviewMaxLines = 20;

    public static SearchApiResponse ToApiResponse(SearchResult result)
    {
        var items = result.Items.Select(ToDto).ToList();
        return new SearchApiResponse(items, result.Total, result.Warnings, result.ElapsedMs);
    }

    public static SearchResultItemDto ToDto(SearchResultItem i) => new(
        i.RepositoryId,
        i.RepositoryName ?? i.RepositoryId.ToString(),
        i.ChunkId,
        i.FileId,
        i.FilePath,
        i.SymbolId,
        i.SymbolName,
        i.Fqn,
        i.Language,
        i.StartLine,
        i.EndLine,
        i.Score,
        i.Origin,
        Truncate(i.Preview, PreviewMaxLines));

    public static string Truncate(string s, int maxLines)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var lines = s.Split('\n');
        if (lines.Length <= maxLines) return s;
        return string.Join("\n", lines.Take(maxLines));
    }

    public static SearchMode ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return SearchMode.Hybrid;
        return Enum.TryParse<SearchMode>(mode, true, out var m) ? m : SearchMode.Hybrid;
    }
}
