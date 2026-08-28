namespace CodeRag.Analyzers.Abstractions;

/// <summary>Результат анализа одного файла.</summary>
public sealed record AnalyzeResult(
    IReadOnlyList<Symbol> Symbols,
    IReadOnlyList<Relation> Relations,
    IReadOnlyList<Chunk> Chunks,
    IReadOnlyList<AnalyzerError> Errors)
{
    public static AnalyzeResult Empty { get; } = new(
        Array.Empty<Symbol>(),
        Array.Empty<Relation>(),
        Array.Empty<Chunk>(),
        Array.Empty<AnalyzerError>());
}
