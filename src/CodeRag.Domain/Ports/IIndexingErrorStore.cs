namespace CodeRag.Domain.Ports;

/// <summary>Хранилище ошибок индексации (таблица indexing_errors, indexing.md §7).</summary>
public interface IIndexingErrorStore
{
    /// <summary>Записать ошибку индексации одного файла (message усекать до ~2KB).</summary>
    Task AddAsync(Guid repositoryId, string filePath, string message, CancellationToken ct = default);
}
