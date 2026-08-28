namespace CodeRag.Analyzers.Abstractions;

/// <summary>
/// Семантический чанк — единица индексации/эмбеддинга (§17).
/// RepositoryId/ProjectId/FileId заполняются Indexer-ом; здесь хранится только Language + границы + контент.
/// </summary>
public sealed record Chunk(
    Guid Id,
    Guid? SymbolId,
    string Language,
    int StartLine,
    int EndLine,
    string Source,
    string ContentHash);
