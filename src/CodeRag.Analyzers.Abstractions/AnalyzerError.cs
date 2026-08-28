namespace CodeRag.Analyzers.Abstractions;

/// <summary>Нефатальная ошибка анализа одного файла — джоб не падает (§49).</summary>
public sealed record AnalyzerError(
    string FilePath,
    string Message,
    int? Line = null,
    int? Column = null);
