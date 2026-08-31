namespace CodeRag.Analyzers.Abstractions;

/// <summary>Содержимое файла для экстрактора связей.</summary>
public sealed record SourceFile(string Path, string Content);

/// <summary>Контекст извлечения связей для одного репозитория.</summary>
public sealed record RelationExtractionContext(Guid RepositoryId, IReadOnlyList<SourceFile> Files);

/// <summary>
/// Извлечение связей между символами для конкретного языка.
/// Реализация получает набор файлов репозитория и возвращает связи,
/// где FromSymbolId/ToSymbolId уже сопоставлены с ID символов из <see cref="ISymbolCatalog"/>.
/// </summary>
public interface IRelationExtractor
{
    string Language { get; }

    Task<IReadOnlyList<Relation>> ExtractAsync(RelationExtractionContext context, CancellationToken ct = default);
}
