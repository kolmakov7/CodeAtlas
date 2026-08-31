using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class RepositoryStore : CodeRag.Domain.Ports.IRepositoryStore, IRepositoryStore
{
    private readonly IDbConnectionFactory _factory;

    public RepositoryStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Repository repository, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO repositories(id, name, git_url, branch, local_path, current_commit, indexed_commit, status, enabled, proxy_id, credential_id, sync_interval_seconds, last_sync_at, last_index_at, last_error)
VALUES (@id,@name,@gitUrl,@branch,@localPath,@currentCommit,@indexedCommit,@status,@enabled,@proxyId,@credentialId,@syncInterval,@lastSyncAt,@lastIndexAt,@lastError)
ON CONFLICT(id) DO UPDATE SET
  name=excluded.name, git_url=excluded.git_url, branch=excluded.branch, local_path=excluded.local_path,
  current_commit=excluded.current_commit, indexed_commit=excluded.indexed_commit, status=excluded.status,
  enabled=excluded.enabled, proxy_id=excluded.proxy_id, credential_id=excluded.credential_id,
  sync_interval_seconds=excluded.sync_interval_seconds, last_sync_at=excluded.last_sync_at,
  last_index_at=excluded.last_index_at, last_error=excluded.last_error;";

        var p = new
        {
            id = repository.Id.ToString(),
            name = repository.Name,
            gitUrl = repository.GitUrl.Value,
            branch = repository.Branch,
            localPath = repository.LocalPath.Value,
            currentCommit = repository.CurrentCommit,
            indexedCommit = repository.IndexedCommit,
            status = repository.Status.ToString(),
            enabled = repository.Enabled ? 1 : 0,
            proxyId = repository.ProxyId?.ToString(),
            credentialId = repository.CredentialId?.ToString(),
            syncInterval = repository.SyncIntervalSeconds,
            lastSyncAt = repository.LastSyncAt?.ToString("o"),
            lastIndexAt = repository.LastIndexAt?.ToString("o"),
            lastError = repository.LastError
        };
        await conn.ExecuteAsync(sql, p).ConfigureAwait(false);
    }

    public async Task<Repository?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<RepoRow>(
            "SELECT * FROM repositories WHERE id = @id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<Repository?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<RepoRow>(
            "SELECT * FROM repositories WHERE name = @name;", new { name }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RepoRow>("SELECT * FROM repositories ORDER BY name;").ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM repositories WHERE id = @id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteCascadeAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            var rid = repositoryId.ToString();
            // Explicit cleanup per storage.md §5 in transaction
            await conn.ExecuteAsync("DELETE FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id = @rid);", new { rid }, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            // Try vec_chunks cleanup if exists
            try
            {
                await conn.ExecuteAsync("DELETE FROM vec_chunks WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            }
            catch { /* ignore if vec_chunks not exists or aux column missing */ }

            await conn.ExecuteAsync("DELETE FROM repositories WHERE id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static Repository Map(RepoRow r)
    {
        var status = Enum.TryParse<RepositoryStatus>(r.status, out var st) ? st : RepositoryStatus.Active;
        var repo = Repository.Rehydrate(
            Guid.Parse(r.id),
            r.name,
            r.git_url,
            r.branch,
            r.local_path,
            r.current_commit,
            r.indexed_commit,
            status,
            r.enabled != 0,
            string.IsNullOrWhiteSpace(r.proxy_id) ? null : Guid.Parse(r.proxy_id),
            string.IsNullOrWhiteSpace(r.credential_id) ? null : Guid.Parse(r.credential_id),
            r.sync_interval_seconds,
            ParseDate(r.last_sync_at),
            ParseDate(r.last_index_at),
            r.last_error);

        // Preserve status transitions that Rehydrate cannot express (Paused/Error without error)
        // Rehydrate already sets status, so only need to ensure error flag if needed.
        if (status == RepositoryStatus.Paused && repo.Status != RepositoryStatus.Paused)
            repo.MarkPaused();
        if (!string.IsNullOrWhiteSpace(r.last_error) && repo.LastError != r.last_error)
        {
            try { repo.MarkError(r.last_error); } catch { }
        }

        return repo;
    }

    private static DateTimeOffset? ParseDate(string? s) =>
        string.IsNullOrWhiteSpace(s) || !DateTimeOffset.TryParse(s, out var d) ? null : d;

    private sealed class RepoRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string git_url { get; set; } = "";
        public string branch { get; set; } = "";
        public string local_path { get; set; } = "";
        public string? current_commit { get; set; }
        public string? indexed_commit { get; set; }
        public string status { get; set; } = "";
        public int enabled { get; set; }
        public string? proxy_id { get; set; }
        public string? credential_id { get; set; }
        public int? sync_interval_seconds { get; set; }
        public string? last_sync_at { get; set; }
        public string? last_index_at { get; set; }
        public string? last_error { get; set; }
    }
}
