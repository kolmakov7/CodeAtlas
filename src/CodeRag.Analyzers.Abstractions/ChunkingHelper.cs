using CodeRag.Analyzers.Abstractions.Chunking;

namespace CodeRag.Analyzers.Abstractions;

/// <summary>
/// Общие утилиты чанкинга (§17). Семантические единицы + fallback sliding window 800/80.
/// [Obsolete] — используйте <see cref="IChunkFactory"/> / <see cref="ISourceSlicer"/> через DI.
/// Оставлен как thin wrapper для совместимости.
/// </summary>
[Obsolete("Use IChunkFactory / ISourceSlicer")]
public static class ChunkingHelper
{
    private static readonly IChunkFactory _defaultFactory = new DefaultChunkFactory();
    private static readonly ISourceSlicer _defaultSlicer = new DefaultSourceSlicer(_defaultFactory);

    public static string ComputeHash(string source) => _defaultFactory.ComputeHash(source);
    public static Chunk CreateChunk(Guid? symbolId, string language, int startLine, int endLine, string source)
        => _defaultFactory.Create(symbolId, language, startLine, endLine, source);
    public static string ExtractLines(string content, int startLine, int endLine)
        => _defaultSlicer.ExtractLines(content, startLine, endLine);
    public static IReadOnlyList<Chunk> SlidingWindow(string content, string language)
        => _defaultSlicer.SlidingWindow(content, language);
    public sealed record CharBudgetSlice(int StartLine, int EndLine, string Text);
    public static IReadOnlyList<CharBudgetSlice> SplitByCharBudget(string source, int startLine, int endLine, int maxChars, int overlapChars, string? header)
    {
        var slices = _defaultSlicer.SplitByCharBudget(source, startLine, endLine, maxChars, overlapChars, header);
        return slices.Select(s => new CharBudgetSlice(s.StartLine, s.EndLine, s.Text)).ToList();
    }
    public static int GetLineFromOffset(string content, int offset) => _defaultSlicer.GetLineFromOffset(content, offset);
}
