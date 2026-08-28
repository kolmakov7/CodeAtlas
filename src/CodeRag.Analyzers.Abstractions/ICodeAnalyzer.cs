namespace CodeRag.Analyzers.Abstractions;

/// <summary>
/// Абстракция анализатора кода. §13 analyzers.md, ADR-003.
/// </summary>
public interface ICodeAnalyzer
{
    /// <summary>Идентификатор языка: csharp | python | sql | vbs | cpp | javascript | go.</summary>
    string Language { get; }

    /// <summary>Может ли анализатор обработать файл (по расширению + shebang/heuristics).</summary>
    bool CanAnalyze(string filePath);

    /// <summary>Анализ файла. Возвращает символы/реляции/чанки + ошибки.</summary>
    Task<AnalyzeResult> AnalyzeAsync(FileContext ctx, CancellationToken ct);
}
