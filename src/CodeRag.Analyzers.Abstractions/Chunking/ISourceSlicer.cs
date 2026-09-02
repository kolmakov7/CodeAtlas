namespace CodeRag.Analyzers.Abstractions.Chunking;

/// <summary>
/// Работа с разбиением исходника по строкам/бюджету — язык-агностична.
/// Инвариантна к Roslyn/ScriptDom/TreeSitter/Regex.
/// </summary>
public interface ISourceSlicer
{
    string ExtractLines(string content, int startLine, int endLine);
    int GetLineFromOffset(string content, int offset);
    IReadOnlyList<Chunk> SlidingWindow(string content, string language);
    IReadOnlyList<CharBudgetSlice> SplitByCharBudget(string source, int startLine, int endLine, int maxChars, int overlapChars, string? header);

    sealed record CharBudgetSlice(int StartLine, int EndLine, string Text);
}
