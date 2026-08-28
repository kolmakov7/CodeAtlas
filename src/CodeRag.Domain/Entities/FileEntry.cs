using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>
/// Файл репозитория (таблица files). Имя FileEntry чтобы избежать коллизии с System.IO.File.
/// Уникальность: (repository_id, path).
/// </summary>
public sealed class FileEntry
{
    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string Path { get; private set; }
    public string? Language { get; private set; }
    public string? Hash { get; private set; }
    public DateTimeOffset? LastIndexedAt { get; private set; }

    private FileEntry(Guid id, Guid repositoryId, string path)
    {
        Id = id;
        RepositoryId = repositoryId;
        Path = path;
    }

    public static FileEntry Create(
        Guid id, Guid repositoryId, string path,
        Guid? projectId = null, string? language = null, string? hash = null,
        DateTimeOffset? lastIndexedAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("File Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");
        if (string.IsNullOrWhiteSpace(path)) throw new DomainValidationException("File Path must not be empty.");

        return new FileEntry(id, repositoryId, path.Trim())
        {
            ProjectId = projectId,
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            Hash = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim(),
            LastIndexedAt = lastIndexedAt
        };
    }

    public void MarkIndexed(string? hash, DateTimeOffset? at = null)
    {
        Hash = string.IsNullOrWhiteSpace(hash) ? Hash : hash.Trim();
        LastIndexedAt = at ?? DateTimeOffset.UtcNow;
    }
}
