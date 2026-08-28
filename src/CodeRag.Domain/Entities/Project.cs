using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Проект внутри репозитория (таблица projects).</summary>
public sealed class Project
{
    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public string? Name { get; private set; }
    public string? Path { get; private set; }
    public string? Language { get; private set; }

    private Project(Guid id, Guid repositoryId)
    {
        Id = id;
        RepositoryId = repositoryId;
    }

    public static Project Create(Guid id, Guid repositoryId, string? name = null, string? path = null, string? language = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Project Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");
        return new Project(id, repositoryId)
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim()
        };
    }
}
