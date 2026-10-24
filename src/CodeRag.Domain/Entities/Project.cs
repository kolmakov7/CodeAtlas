using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Проект внутри репозитория (таблица projects).</summary>
public sealed class Project
{
    private readonly List<Guid> _repositoryIds = new();

    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public string? Name { get; private set; }
    public string? Path { get; private set; }
    public string? Language { get; private set; }

    public IReadOnlyList<Guid> RepositoryIds => _repositoryIds;

    private Project(Guid id, Guid repositoryId)
    {
        Id = id;
        RepositoryId = repositoryId;
    }

    public static Project Create(
        Guid id, Guid repositoryId, string? name = null, string? path = null, string? language = null,
        IEnumerable<Guid>? repositoryIds = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Project Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");

        var project = new Project(id, repositoryId)
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim()
        };
        project.SetRepositories(repositoryIds ?? new[] { repositoryId });
        return project;
    }

    public void SetRepositories(IEnumerable<Guid> repositoryIds)
    {
        _repositoryIds.Clear();
        foreach (var repositoryId in repositoryIds.Distinct())
        {
            if (repositoryId == Guid.Empty)
                throw new DomainValidationException("RepositoryId must not be empty.");
            _repositoryIds.Add(repositoryId);
        }
        if (_repositoryIds.Count == 0)
            throw new DomainValidationException("A project must be linked to at least one repository.");
    }

    public void Rename(string? name, string? path, string? language)
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
    }

    public static Project Rehydrate(
        Guid id, Guid repositoryId, string? name, string? path, string? language,
        IEnumerable<Guid>? repositoryIds = null)
    {
        var project = new Project(id, repositoryId)
        {
            Name = name,
            Path = path,
            Language = language
        };
        var ids = repositoryIds?.Distinct().ToList();
        if (ids is { Count: > 0 }) project._repositoryIds.AddRange(ids);
        else project._repositoryIds.Add(repositoryId);
        return project;
    }
}
