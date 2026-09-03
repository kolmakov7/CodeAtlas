namespace CodeRag.Analyzers.Abstractions;

/// <summary>Содержимое файла для экстрактора связей.</summary>
public sealed record SourceFile(string Path, string Content);

/// <summary>Контекст извлечения связей для одного репозитория.</summary>
public sealed record RelationExtractionContext(Guid RepositoryId, IReadOnlyList<SourceFile> Files);

/// <summary>Прогресс whole-repository извлечения связей (по файлам).</summary>
public sealed record RelationExtractionProgress(int FilesTotal, int FilesDone, string? CurrentFile);

/// <summary>
/// Whole-repository извлечение связей между символами для конкретного языка.
/// Реализация получает набор файлов репозитория и возвращает связи,
/// где FromSymbolId/ToSymbolId уже сопоставлены с ID символов из <see cref="ISymbolCatalog"/>.
/// Языковые детали (какие пути относятся к языку) живут в реализации,
/// а не в orchestrator-слое (Indexer).
/// Переименовано из IRelationExtractor для ясности (per-file relations живут внутри ICodeAnalyzer).
/// </summary>
public interface IRepositoryRelationExtractor
{
    string Language { get; }

    /// <summary>Относится ли путь к файлу к этому языку (расширение + исключения).</summary>
    bool IsRelevantPath(string path);

    Task<IReadOnlyList<Relation>> ExtractAsync(
        RelationExtractionContext context,
        CancellationToken ct = default,
        IProgress<RelationExtractionProgress>? progress = null);
}
