using System.Text.RegularExpressions;

namespace CodeRag.Analyzers.Abstractions.Chunking;

public sealed class DefaultSourceSlicer : ISourceSlicer
{
    private readonly IChunkFactory _factory;

    public DefaultSourceSlicer(IChunkFactory factory) => _factory = factory;

    public string ExtractLines(string content, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var lines = content.Split('\n');
        var s = Math.Clamp(startLine - 1, 0, lines.Length - 1);
        var e = Math.Clamp(endLine - 1, s, lines.Length - 1);
        var slice = lines[s..(e + 1)];
        return string.Join("\n", slice);
    }

    public int GetLineFromOffset(string content, int offset)
    {
        if (offset <= 0) return 1;
        if (offset > content.Length) offset = content.Length;
        var s = content.AsSpan(0, offset);
        int count = 1;
        foreach (var c in s) if (c == '\n') count++;
        return count;
    }

    public IReadOnlyList<Chunk> SlidingWindow(string content, string language)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<Chunk>();
        var tokens = Regex.Split(content, @"\s+").Where(t => !string.IsNullOrEmpty(t)).ToArray();
        const int window = 800;
        const int overlap = 80;
        const int step = window - overlap;

        if (tokens.Length <= window)
        {
            var lines = content.Split('\n').Length;
            var chunk = _factory.Create(null, language, 1, lines, content.TrimEnd());
            return new[] { chunk };
        }

        var chunks = new List<Chunk>();
        var totalLines = content.Split('\n').Length;
        for (int i = 0; i < tokens.Length; i += step)
        {
            var sliceTokens = tokens.Skip(i).Take(window).ToArray();
            if (sliceTokens.Length == 0) break;
            var source = string.Join(" ", sliceTokens);
            var startLine = (int)((double)i / tokens.Length * totalLines) + 1;
            var endLine = (int)((double)Math.Min(i + window, tokens.Length) / tokens.Length * totalLines);
            if (endLine < startLine) endLine = startLine;
            if (endLine > totalLines) endLine = totalLines;
            if (string.IsNullOrWhiteSpace(source)) continue;
            chunks.Add(_factory.Create(null, language, startLine, endLine, source));
            if (i + window >= tokens.Length) break;
        }

        if (chunks.Count == 0)
            chunks.Add(_factory.Create(null, language, 1, totalLines, content.TrimEnd()));
        return chunks;
    }

    public IReadOnlyList<ISourceSlicer.CharBudgetSlice> SplitByCharBudget(string source, int startLine, int endLine, int maxChars, int overlapChars, string? header)
    {
        if (string.IsNullOrWhiteSpace(source) || maxChars <= 0) return Array.Empty<ISourceSlicer.CharBudgetSlice>();
        if (source.Length <= maxChars) return Array.Empty<ISourceSlicer.CharBudgetSlice>();

        var headerLen = string.IsNullOrWhiteSpace(header) ? 0 : header.Length + 1;
        var windowChars = Math.Max(1, maxChars - headerLen);
        var overlap = Math.Clamp(overlapChars, 0, windowChars - 1);
        var step = windowChars - overlap;

        var slices = new List<ISourceSlicer.CharBudgetSlice>();
        var totalLen = source.Length;
        int windowStart = 0;
        var isFirst = true;
        while (windowStart < totalLen)
        {
            int windowEnd = Math.Min(windowStart + windowChars, totalLen);
            if (windowEnd < totalLen)
            {
                var nl = source.LastIndexOf('\n', windowEnd - 1);
                if (nl > windowStart) windowEnd = nl + 1;
            }
            var slice = source[windowStart..windowEnd];
            var sLine = startLine + GetLineFromOffset(source, windowStart) - 1;
            var eLine = startLine + GetLineFromOffset(source, Math.Min(windowEnd - (slice.EndsWith("\n") ? 1 : 0), totalLen - 1)) - 1;
            sLine = Math.Clamp(sLine, startLine, Math.Max(endLine, startLine));
            eLine = Math.Clamp(eLine, sLine, Math.Max(endLine, startLine));
            var text = isFirst ? slice : (header! + "\n" + slice.TrimStart());
            if (!string.IsNullOrWhiteSpace(text))
                slices.Add(new ISourceSlicer.CharBudgetSlice(sLine, eLine, text));
            if (windowEnd >= totalLen) break;
            var nextStart = Math.Min(windowStart + step, windowEnd);
            windowStart = nextStart > windowStart ? nextStart : windowStart + 1;
            isFirst = false;
        }
        return slices.Count > 0 ? slices : Array.Empty<ISourceSlicer.CharBudgetSlice>();
    }
}
