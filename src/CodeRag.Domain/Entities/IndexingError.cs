using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Ошибка индексации одного файла (таблица indexing_errors, indexing.md §7).</summary>
public sealed class IndexingError
{
    public Guid Id { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public string? FilePath { get; private set; }
    public string Message { get; private set; }
    public DateTimeOffset At { get; private set; }

    private IndexingError(Guid id, string message, DateTimeOffset at)
    {
        Id = id;
        Message = message;
        At = at;
    }

    public static IndexingError Create(
        Guid id, string message,
        Guid? repositoryId = null, string? filePath = null, DateTimeOffset? at = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("IndexingError Id must not be empty.");
        if (string.IsNullOrWhiteSpace(message)) throw new DomainValidationException("Message must not be empty.");
        return new IndexingError(id, message.Trim(), at ?? DateTimeOffset.UtcNow)
        {
            RepositoryId = repositoryId,
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim()
        };
    }
}
