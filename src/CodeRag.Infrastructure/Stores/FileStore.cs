using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class FileStore : CodeRag.Domain.Ports.IFileStore
{
    private readonly IDbConnectionFactory _factory;
    public FileStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(FileEntry file, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO files(id, repository_id, project_id, path, language, hash, last_indexed_at)
VALUES (@id,@repo,@proj,@path,@lang,@hash,@last)
ON CONFLICT(repository_id, path) DO UPDATE SET
  id=excluded.id, project_id=excluded.project_id, language=excluded.language, hash=excluded.hash, last_indexed_at=excluded.last_indexed_at;
-- Also handle id PK conflict
";
        // Dapper will execute as is; SQLite ON CONFLICT for UNIQUE(repo,path) — need separate upsert by id as well.
        // We'll try insert or replace via two statements: first try by id.
        const string sql2 = @"
INSERT INTO files(id, repository_id, project_id, path, language, hash, last_indexed_at)
VALUES (@id,@repo,@proj,@path,@lang,@hash,@last)
ON CONFLICT(id) DO UPDATE SET
  repository_id=excluded.repository_id, project_id=excluded.project_id, path=excluded.path,
  language=excluded.language, hash=excluded.hash, last_indexed_at=excluded.last_indexed_at;";

        var p = new
        {
            id = file.Id.ToString(),
            repo = file.RepositoryId.ToString(),
            proj = file.ProjectId?.ToString(),
            path = file.Path,
            lang = file.Language,
            hash = file.Hash,
            last = file.LastIndexedAt?.ToString("o")
        };

        try
        {
            await conn.ExecuteAsync(sql2, p).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlDialect.IsUniqueViolation(_factory, ex))
        {
            await conn.ExecuteAsync(sql, p).ConfigureAwait(false);
        }
    }

    public async Task UpsertBatchAsync(IEnumerable<FileEntry> files, CancellationToken ct = default)
    {
        var list = files.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var f in list)
            {
                var p = new
                {
                    id = f.Id.ToString(),
                    repo = f.RepositoryId.ToString(),
                    proj = f.ProjectId?.ToString(),
                    path = f.Path,
                    lang = f.Language,
                    hash = f.Hash,
                    last = f.LastIndexedAt?.ToString("o")
                };
                await conn.ExecuteAsync(
                    @"INSERT INTO files(id, repository_id, project_id, path, language, hash, last_indexed_at)
                      VALUES (@id,@repo,@proj,@path,@lang,@hash,@last)
                      ON CONFLICT(id) DO UPDATE SET repository_id=excluded.repository_id, project_id=excluded.project_id, path=excluded.path, language=excluded.language, hash=excluded.hash, last_indexed_at=excluded.last_indexed_at;",
                    p, transaction: tx).ConfigureAwait(false);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<FileEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<FileRow>("SELECT * FROM files WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<FileEntry?> GetByPathAsync(Guid repositoryId, string path, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<FileRow>(
            "SELECT * FROM files WHERE repository_id=@repo AND path=@path;",
            new { repo = repositoryId.ToString(), path }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<FileEntry>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<FileRow>("SELECT * FROM files WHERE repository_id=@repo ORDER BY path;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM files WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM files WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
    }

    private static FileEntry Map(FileRow r)
    {
        return FileEntry.Create(
            Guid.Parse(r.id),
            Guid.Parse(r.repository_id),
            r.path,
            string.IsNullOrWhiteSpace(r.project_id) ? null : Guid.Parse(r.project_id),
            r.language,
            r.hash,
            string.IsNullOrWhiteSpace(r.last_indexed_at) ? null : DateTimeOffset.Parse(r.last_indexed_at));
    }

    private sealed class FileRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string? project_id { get; set; }
        public string path { get; set; } = "";
        public string? language { get; set; }
        public string? hash { get; set; }
        public string? last_indexed_at { get; set; }
    }
}
