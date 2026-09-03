using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class RepositoryStore : CodeRag.Domain.Ports.IRepositoryStore
{
    private readonly IDbConnectionFactory _factory;

    public RepositoryStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Repository repository, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
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
            groupId = repository.GroupId?.ToString(),
            proxyId = repository.ProxyId?.ToString(),
            credentialId = repository.CredentialId?.ToString(),
            syncInterval = repository.SyncIntervalSeconds,
            lastSyncAt = repository.LastSyncAt?.ToString("o"),
            lastIndexAt = repository.LastIndexAt?.ToString("o"),
            lastError = repository.LastError,
            filterEnabled = repository.DirectoryFilterEnabled ? 1 : 0,
            allowedDirs = repository.AllowedDirectories.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(repository.AllowedDirectories)
        };

        const string sql = @"
 INSERT INTO repositories(id, name, git_url, branch, local_path, current_commit, indexed_commit, status, enabled, group_id, proxy_id, credential_id, sync_interval_seconds, last_sync_at, last_index_at, last_error, filter_enabled, allowed_directories)
 VALUES (@id,@name,@gitUrl,@branch,@localPath,@currentCommit,@indexedCommit,@status,@enabled,@groupId,@proxyId,@credentialId,@syncInterval,@lastSyncAt,@lastIndexAt,@lastError,@filterEnabled,@allowedDirs)
 ON CONFLICT(id) DO UPDATE SET
   name=excluded.name, git_url=excluded.git_url, branch=excluded.branch, local_path=excluded.local_path,
   current_commit=excluded.current_commit, indexed_commit=excluded.indexed_commit, status=excluded.status,
   enabled=excluded.enabled, group_id=excluded.group_id, proxy_id=excluded.proxy_id, credential_id=excluded.credential_id,
   sync_interval_seconds=excluded.sync_interval_seconds, last_sync_at=excluded.last_sync_at,
   last_index_at=excluded.last_index_at, last_error=excluded.last_error, filter_enabled=excluded.filter_enabled, allowed_directories=excluded.allowed_directories;";

        try
        {
            await conn.ExecuteAsync(sql, p).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlDialect.IsUniqueViolation(_factory, ex))
        {
            // UNIQUE constraint failed (likely repositories.name) — fallback to UPDATE by id
            // Happens when two concurrent upserts race or name already exists with different id
            const string updateSql = @"UPDATE repositories SET name=@name, git_url=@gitUrl, branch=@branch, local_path=@localPath, current_commit=@currentCommit, indexed_commit=@indexedCommit, status=@status, enabled=@enabled, group_id=@groupId, proxy_id=@proxyId, credential_id=@credentialId, sync_interval_seconds=@syncInterval, last_sync_at=@lastSyncAt, last_index_at=@lastIndexAt, last_error=@lastError, filter_enabled=@filterEnabled, allowed_directories=@allowedDirs WHERE id=@id";
            await conn.ExecuteAsync(updateSql, p).ConfigureAwait(false);
        }
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

    /// <summary>
    /// Кросс-БД удаление: vector-БД (vec-таблицы), chunks-БД (chunks + chunks_fts),
    /// main-БД (vector_entries по repository_id, indexing_errors, activity_log, repositories).
    /// Транзакции не пересекают БД — при сбое возможна краткая рассинхронизация (принято для alpha).
    /// </summary>
    public async Task DeleteCascadeAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var rid = repositoryId.ToString();

        // 1) Vector DB (vec0 tables in SQLite / pgvector tables in Postgres).
        using (var vecConn = await _factory.CreateVecAsync(ct).ConfigureAwait(false))
        using (var vecTx = vecConn.BeginTransaction())
        {
            foreach (var table in new[] { "vec_chunks_code", "vec_chunks_text", "vec_chunks" })
            {
                try
                {
                    await vecConn.ExecuteAsync($"DELETE FROM {table} WHERE repository_id = @rid;", new { rid }, transaction: vecTx).ConfigureAwait(false);
                }
                catch { /* ignore if table not created yet */ }
            }
            vecTx.Commit();
        }

        // 2) Chunks DB: chunks + FTS.
        using (var chunksConn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false))
        using (var chunksTx = chunksConn.BeginTransaction())
        {
            await chunksConn.ExecuteAsync("DELETE FROM chunks WHERE repository_id = @rid;", new { rid }, transaction: chunksTx).ConfigureAwait(false);
            await chunksConn.ExecuteAsync("DELETE FROM chunks_fts WHERE repository_id = @rid;", new { rid }, transaction: chunksTx).ConfigureAwait(false);
            chunksTx.Commit();
        }

        // 3) Main DB: vector_entries (repository_id column added by the consolidated schema),
        //    job errors / activity, and the repository itself.
        using (var conn = await _factory.CreateAsync(ct).ConfigureAwait(false))
        using (var tx = conn.BeginTransaction())
        {
            await conn.ExecuteAsync("DELETE FROM vector_entries WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            // Repo-scoped job errors / activity would otherwise survive with repository_id=NULL (SET NULL)
            await conn.ExecuteAsync("DELETE FROM indexing_errors WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM activity_log WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM repositories WHERE id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            tx.Commit();
        }

        // Reclaim space: SQLite keeps free pages after DELETE — VACUUM rewrites the file (SQLite only).
        if (!SqlDialect.IsPostgres(_factory))
        {
            foreach (var open in new[] { () => _factory.CreateAsync(ct), () => _factory.CreateChunksAsync(ct), () => _factory.CreateVecAsync(ct) })
            {
                try
                {
                    using var vacConn = await open().ConfigureAwait(false);
                    await vacConn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
                    await vacConn.ExecuteAsync("VACUUM;").ConfigureAwait(false);
                }
                catch { /* best-effort, dashboard will still show correct logical counts */ }
            }
        }
    }

    public async Task<string?> GetLocalPathAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT local_path FROM repositories WHERE id = @id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    private static Repository Map(RepoRow r)
    {
        var status = Enum.TryParse<RepositoryStatus>(r.status, out var st) ? st : RepositoryStatus.Active;
        Guid? groupId = string.IsNullOrWhiteSpace(r.group_id) ? null : Guid.Parse(r.group_id);
        var allowed = ParseAllowed(r.allowed_directories);
        var filterEnabled = r.filter_enabled.HasValue ? r.filter_enabled.Value != 0 : false;
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
            r.last_error,
            groupId,
            filterEnabled,
            allowed);

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

    private static IReadOnlyList<string> ParseAllowed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
            return arr ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
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
        public string? group_id { get; set; }
        public int? sync_interval_seconds { get; set; }
        public string? last_sync_at { get; set; }
        public string? last_index_at { get; set; }
        public string? last_error { get; set; }
        public int? filter_enabled { get; set; }
        public string? allowed_directories { get; set; }
    }
}
