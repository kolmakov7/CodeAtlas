using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeRag.Analyzers.Abstractions;

/// <summary>
/// Общие утилиты чанкинга (§17). Семантические единицы + fallback sliding window 800/80.
/// </summary>
public static class ChunkingHelper
{
    /// <summary>SHA256 hex lower по UTF8.</summary>
    public static string ComputeHash(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Создать Chunk DTO с хешем.</summary>
    public static Chunk CreateChunk(Guid? symbolId, string language, int startLine, int endLine, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("source must not be empty", nameof(source));
        var hash = ComputeHash(source);
        return new Chunk(Guid.NewGuid(), symbolId, language, startLine, endLine, source, hash);
    }

    /// <summary>Вырезать строки [startLine..endLine] (1-indexed) из content.</summary>
    public static string ExtractLines(string content, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var lines = content.Split('\n');
        // lines are 0-indexed, startLine 1-indexed
        var s = Math.Clamp(startLine - 1, 0, lines.Length - 1);
        var e = Math.Clamp(endLine - 1, s, lines.Length - 1);
        var slice = lines[s..(e + 1)];
        // Preserve original newlines (join with \n, trim trailing \r already split)
        return string.Join("\n", slice);
    }

    /// <summary>Fallback sliding window 800 токенов overlap 80 для неизвестных языков (§17).</summary>
    public static IReadOnlyList<Chunk> SlidingWindow(string content, string language)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<Chunk>();

        // Токенизация по whitespace
        var tokens = Regex.Split(content, @"\s+").Where(t => !string.IsNullOrEmpty(t)).ToArray();
        const int window = 800;
        const int overlap = 80;
        const int step = window - overlap;

        if (tokens.Length <= window)
        {
            var lines = content.Split('\n').Length;
            var chunk = CreateChunk(null, language, 1, lines, content.TrimEnd());
            return new[] { chunk };
        }

        var chunks = new List<Chunk>();
        var totalLines = content.Split('\n').Length;

        for (int i = 0; i < tokens.Length; i += step)
        {
            var sliceTokens = tokens.Skip(i).Take(window).ToArray();
            if (sliceTokens.Length == 0) break;
            var source = string.Join(" ", sliceTokens);
            // Аппроксимация строк: равномерно распределить
            var startLine = (int)((double)i / tokens.Length * totalLines) + 1;
            var endLine = (int)((double)Math.Min(i + window, tokens.Length) / tokens.Length * totalLines);
            if (endLine < startLine) endLine = startLine;
            if (endLine > totalLines) endLine = totalLines;
            if (string.IsNullOrWhiteSpace(source)) continue;
            chunks.Add(CreateChunk(null, language, startLine, endLine, source));
            if (i + window >= tokens.Length) break;
        }

        // Если по какой-то причине пусто — вернуть один чанк с исходным контентом
        if (chunks.Count == 0)
        {
            chunks.Add(CreateChunk(null, language, 1, totalLines, content.TrimEnd()));
        }

        return chunks;
    }

    /// <summary>Подсчитать номер строки по offset в content (1-indexed).</summary>
    public static int GetLineFromOffset(string content, int offset)
    {
        if (offset <= 0) return 1;
        if (offset > content.Length) offset = content.Length;
        var s = content.AsSpan(0, offset);
        int count = 1;
        foreach (var c in s) if (c == '\n') count++;
        return count;
    }
}
