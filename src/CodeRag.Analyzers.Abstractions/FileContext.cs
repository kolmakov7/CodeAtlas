namespace CodeRag.Analyzers.Abstractions;

/// <summary>Контекст анализируемого файла.</summary>
public sealed record FileContext(
    Guid RepositoryId,
    Guid? ProjectId,
    string FilePath,
    string Content,
    string Language,
    int MaxChunkChars = 1500);
