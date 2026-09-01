using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class RepositoryGroupStore : CodeRag.Domain.Ports.IRepositoryGroupStore
{
    private readonly IDbConnectionFactory _factory;
    public RepositoryGroupStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(RepositoryGroup group, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO repository_groups(id, name, proxy_id, credential_id, created_at)
VALUES (@id,@name,@proxyId,@credId,@created)
ON CONFLICT(id) DO UPDATE SET
  name=excluded.name, proxy_id=excluded.proxy_id, credential_id=excluded.credential_id;";
        await conn.ExecuteAsync(sql, new
        {
            id = group.Id.ToString(),
            name = group.Name,
            proxyId = group.ProxyId?.ToString(),
            credId = group.CredentialId?.ToString(),
            created = group.CreatedAt.ToString("o")
        }).ConfigureAwait(false);
    }

    public async Task<RepositoryGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<Row>("SELECT * FROM repository_groups WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<RepositoryGroup?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<Row>("SELECT * FROM repository_groups WHERE name=@name;", new { name }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<RepositoryGroup>> ListAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Row>("SELECT * FROM repository_groups ORDER BY name;").ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        // Prevent deletion of Default group
        var row = await conn.QuerySingleOrDefaultAsync<Row>("SELECT * FROM repository_groups WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        if (row is not null && string.Equals(row.name, "Default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot delete Default group.");
        await conn.ExecuteAsync("DELETE FROM repository_groups WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RepositoryGroup>> GetByProxyIdAsync(Guid proxyId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Row>("SELECT * FROM repository_groups WHERE proxy_id=@pid;", new { pid = proxyId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<RepositoryGroup>> GetByCredentialIdAsync(Guid credentialId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Row>("SELECT * FROM repository_groups WHERE credential_id=@cid;", new { cid = credentialId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<CodeRag.Domain.Entities.Repository>> GetRepositoriesByGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RepoRow>("SELECT * FROM repositories WHERE group_id=@gid;", new { gid = groupId.ToString() }).ConfigureAwait(false);
        return rows.Select(MapRepo).ToList();
    }

    private static RepositoryGroup Map(Row r)
    {
        var created = DateTimeOffset.TryParse(r.created_at, out var d) ? d : DateTimeOffset.UtcNow;
        Guid? proxy = string.IsNullOrWhiteSpace(r.proxy_id) ? null : Guid.Parse(r.proxy_id);
        Guid? cred = string.IsNullOrWhiteSpace(r.credential_id) ? null : Guid.Parse(r.credential_id);
        return RepositoryGroup.Rehydrate(Guid.Parse(r.id), r.name, proxy, cred, created);
    }

    private static CodeRag.Domain.Entities.Repository MapRepo(RepoRow r)
    {
        var status = Enum.TryParse<CodeRag.Domain.Enums.RepositoryStatus>(r.status, out var st) ? st : CodeRag.Domain.Enums.RepositoryStatus.Active;
        Guid? groupId = string.IsNullOrWhiteSpace(r.group_id) ? null : Guid.Parse(r.group_id);
        return CodeRag.Domain.Entities.Repository.RehydrateWithGroup(
            Guid.Parse(r.id), r.name, r.git_url, r.branch, r.local_path,
            r.current_commit, r.indexed_commit, status, r.enabled != 0,
            groupId, r.sync_interval_seconds,
            ParseDate(r.last_sync_at), ParseDate(r.last_index_at), r.last_error);
    }

    private static DateTimeOffset? ParseDate(string? s) => string.IsNullOrWhiteSpace(s) || !DateTimeOffset.TryParse(s, out var d) ? null : d;

    private sealed class Row
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string? proxy_id { get; set; }
        public string? credential_id { get; set; }
        public string created_at { get; set; } = "";
    }

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
        public string? group_id { get; set; }
        public string? proxy_id { get; set; }
        public string? credential_id { get; set; }
        public int? sync_interval_seconds { get; set; }
        public string? last_sync_at { get; set; }
        public string? last_index_at { get; set; }
        public string? last_error { get; set; }
    }
}
