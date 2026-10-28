using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class ProjectStore : IProjectStore
{
    private readonly IDbConnectionFactory _factory;

    public ProjectStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Project project, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(
            @"INSERT INTO projects(id, repository_id, name, path, language)
              VALUES(@id, @repo, @name, @path, @language)
              ON CONFLICT(id) DO UPDATE SET
                repository_id=excluded.repository_id,
                name=excluded.name,
                path=excluded.path,
                language=excluded.language;",
            new
            {
                id = project.Id.ToString(),
                repo = project.RepositoryId?.ToString(),
                name = project.Name,
                path = project.Path,
                language = project.Language
            }, tx).ConfigureAwait(false);
        await ReplaceRepositoriesAsync(conn, tx, project.Id, project.RepositoryIds, ct).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<ProjectRow>(
            "SELECT * FROM projects WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        if (row is null) return null;
        var repos = (await conn.QueryAsync<string>(
            "SELECT repository_id FROM project_repositories WHERE project_id=@id ORDER BY created_at;",
            new { id = id.ToString() }).ConfigureAwait(false)).ToList();
        return Map(row, repos);
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<ProjectRow>("SELECT * FROM projects ORDER BY COALESCE(name, id);").ConfigureAwait(false)).ToList();
        var links = (await conn.QueryAsync<ProjectRepositoryRow>(
            "SELECT project_id, repository_id FROM project_repositories ORDER BY created_at;").ConfigureAwait(false)).ToList();
        var byProject = links.GroupBy(l => l.project_id).ToDictionary(g => g.Key, g => g.Select(x => x.repository_id).ToList());
        return rows.Select(r => Map(r, byProject.TryGetValue(r.id, out var repos) ? repos : new List<string>())).ToList();
    }

    public async Task<IReadOnlyList<Project>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<ProjectRow>(
            @"SELECT p.* FROM projects p
              JOIN project_repositories pr ON pr.project_id = p.id
              WHERE pr.repository_id=@repo
              ORDER BY COALESCE(p.name, p.id);",
            new { repo = repositoryId.ToString() }).ConfigureAwait(false)).ToList();
        if (rows.Count == 0) return Array.Empty<Project>();
        var ids = rows.Select(r => r.id).ToList();
        var links = (await conn.QueryAsync<ProjectRepositoryRow>(
            "SELECT project_id, repository_id FROM project_repositories WHERE project_id IN @ids ORDER BY created_at;",
            new { ids }).ConfigureAwait(false)).ToList();
        var byProject = links.GroupBy(l => l.project_id).ToDictionary(g => g.Key, g => g.Select(x => x.repository_id).ToList());
        return rows.Select(r => Map(r, byProject.TryGetValue(r.id, out var repos) ? repos : new List<string>())).ToList();
    }

    public async Task<Project?> GetDefaultForRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var projects = await GetByRepositoryAsync(repositoryId, ct).ConfigureAwait(false);
        return projects.FirstOrDefault();
    }

    public async Task SetRepositoriesAsync(Guid projectId, IEnumerable<Guid> repositoryIds, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        await ReplaceRepositoriesAsync(conn, tx, projectId, repositoryIds.Distinct().Select(x => x).ToList(), ct).ConfigureAwait(false);
        tx.Commit();
    }

    private static async Task ReplaceRepositoriesAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid projectId,
        IReadOnlyList<Guid> repositoryIds,
        CancellationToken ct)
    {
        var ids = repositoryIds.Where(x => x != Guid.Empty).Distinct().ToList();
        await conn.ExecuteAsync("DELETE FROM project_repositories WHERE project_id=@id;",
            new { id = projectId.ToString() }, tx).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("o");
        foreach (var repoId in ids)
        {
            await conn.ExecuteAsync(
                "INSERT INTO project_repositories(project_id, repository_id, created_at) VALUES(@p, @r, @at) ON CONFLICT DO NOTHING;",
                new { p = projectId.ToString(), r = repoId.ToString(), at = now }, tx).ConfigureAwait(false);
        }
        await conn.ExecuteAsync("UPDATE projects SET repository_id=@repo WHERE id=@id;",
            new { repo = ids.Count == 0 ? null : ids[0].ToString(), id = projectId.ToString() }, tx).ConfigureAwait(false);
    }

    private static Project Map(ProjectRow row, IReadOnlyList<string> repositoryIds)
        => Project.Rehydrate(
            Guid.Parse(row.id),
            string.IsNullOrWhiteSpace(row.repository_id) ? null : Guid.Parse(row.repository_id),
            row.name,
            row.path,
            row.language,
            repositoryIds.Select(Guid.Parse).ToList());

    private sealed class ProjectRow
    {
        public string id { get; set; } = "";
        public string? repository_id { get; set; }
        public string? name { get; set; }
        public string? path { get; set; }
        public string? language { get; set; }
    }

    private sealed class ProjectRepositoryRow
    {
        public string project_id { get; set; } = "";
        public string? repository_id { get; set; }
    }
}
