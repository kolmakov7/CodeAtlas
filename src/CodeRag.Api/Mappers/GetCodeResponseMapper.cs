using CodeRag.Api.Models;
using CodeRag.Search.Models;

namespace CodeRag.Api.Mappers;

/// <summary>
/// Маппер SourceSlice → GetCodeResponseDto. Убирает анонимный тип из контроллера.
/// </summary>
public static class GetCodeResponseMapper
{
    private const int PreviewMaxLines = 20;

    public static GetCodeResponseDto ToDto(SourceSlice slice)
    {
        var preview = Truncate(slice.Preview, PreviewMaxLines);
        return new GetCodeResponseDto(
            slice.RepositoryId,
            slice.FileId,
            slice.Path,
            slice.Language,
            slice.StartLine,
            slice.EndLine,
            slice.TotalLines,
            slice.Content,
            preview);
    }

    private static string Truncate(string s, int maxLines)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var lines = s.Split('\n');
        if (lines.Length <= maxLines) return s;
        return string.Join("\n", lines.Take(maxLines));
    }

    /// <summary>Парсинг ?lines= "10-20" / "10:20" / "10,20" / "10"</summary>
    public static (int? Start, int? End) ParseLines(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines)) return (null, null);
        var s = lines.Trim();
        char[] seps = ['-', ':', ','];
        var sep = seps.FirstOrDefault(c => s.Contains(c));
        if (sep != default)
        {
            var parts = s.Split(sep);
            if (parts.Length == 2)
            {
                int? start = int.TryParse(parts[0].Trim(), out var a) ? a : null;
                int? end = int.TryParse(parts[1].Trim(), out var b) ? b : null;
                return (start, end);
            }
        }
        else if (int.TryParse(s, out var single))
        {
            return (single, single);
        }
        return (null, null);
    }
}
