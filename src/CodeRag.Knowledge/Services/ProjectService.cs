using System.Security.Cryptography;
using System.Text;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Ports;

namespace CodeRag.Knowledge.Services;

public sealed class ProjectService
{
    private readonly IProjectStore _projects;
    private readonly IRepositoryStore _repositories;

    public ProjectService(IProjectStore projects, IRepositoryStore repositories)
    {
        _projects = projects;
        _repositories = repositories;
    }

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        => _projects.ListAsync(ct);

    public async Task<Project> GetAsync(Guid id, CancellationToken ct = default)
    {
        var project = await _projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        return project ?? throw new DomainValidationException($"Project {id} not found.");
    }

    public async Task<IReadOnlyList<Project>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
        => await _projects.GetByRepositoryAsync(repositoryId, ct).ConfigureAwait(false);

    public async Task<Project> CreateAsync(
        string? name, IReadOnlyList<Guid> repositoryIds, string? path = null, string? language = null,
        CancellationToken ct = default)
    {
        if (repositoryIds.Count == 0)
            throw new DomainValidationException("A project must be linked to at least one repository.");
        foreach (var repositoryId in repositoryIds.Distinct())
        {
            var repository = await _repositories.GetByIdAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is null) throw new DomainValidationException($"Repository {repositoryId} not found.");
        }
        var id = Guid.NewGuid();
        var project = Project.Create(id, repositoryIds[0], name, path, language, repositoryIds);
        await _projects.UpsertAsync(project, ct).ConfigureAwait(false);
        return project;
    }

    public async Task SetRepositoriesAsync(Guid projectId, IReadOnlyList<Guid> repositoryIds, CancellationToken ct = default)
    {
        _ = await GetAsync(projectId, ct).ConfigureAwait(false);
        if (repositoryIds.Count == 0)
            throw new DomainValidationException("A project must be linked to at least one repository.");
        foreach (var repositoryId in repositoryIds.Distinct())
        {
            var repository = await _repositories.GetByIdAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is null) throw new DomainValidationException($"Repository {repositoryId} not found.");
        }
        await _projects.SetRepositoriesAsync(projectId, repositoryIds, ct).ConfigureAwait(false);
    }

    public async Task<Project> EnsureDefaultForRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var existing = await _projects.GetDefaultForRepositoryAsync(repositoryId, ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        var repository = await _repositories.GetByIdAsync(repositoryId, ct).ConfigureAwait(false);
        if (repository is null) throw new DomainValidationException($"Repository {repositoryId} not found.");

        var project = Project.Create(DefaultProjectId(repositoryId), repositoryId, repository.Name, null, null,
            new[] { repositoryId });
        await _projects.UpsertAsync(project, ct).ConfigureAwait(false);
        return project;
    }

    public async Task<Project?> ResolveAsync(Guid? projectId, Guid? repositoryId, CancellationToken ct = default)
    {
        if (projectId is not null)
        {
            var project = await GetAsync(projectId.Value, ct).ConfigureAwait(false);
            if (repositoryId is not null && !project.RepositoryIds.Contains(repositoryId.Value))
                throw new DomainValidationException(
                    $"Repository {repositoryId} is not linked to project {projectId}.");
            return project;
        }
        if (repositoryId is not null)
            return await EnsureDefaultForRepositoryAsync(repositoryId.Value, ct).ConfigureAwait(false);
        return null;
    }

    public static Guid DefaultProjectId(Guid repositoryId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("codeatlas:default-project:" + repositoryId));
        var guid = new byte[16];
        Array.Copy(bytes, guid, 16);
        return new Guid(guid);
    }
}
