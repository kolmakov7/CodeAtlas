using System.Data;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class KnowledgeStore : IKnowledgeStore
{
    private readonly IDbConnectionFactory _factory;

    public KnowledgeStore(IDbConnectionFactory factory) => _factory = factory;

    private bool IsPostgres => SqlDialect.IsPostgres(_factory);

    public async Task CreateAsync(KnowledgeEntry entry, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(InsertSql, Params(entry), tx).ConfigureAwait(false);
        await UpsertFtsAsync(conn, tx, entry.Id, entry.Title, entry.Content).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task<KnowledgeEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<KnowledgeRow>(
            "SELECT * FROM knowledge_entries WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<KnowledgeEntry>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<KnowledgeRow>(
            "SELECT * FROM knowledge_entries WHERE id IN @ids;",
            new { ids = ids.Select(x => x.ToString()).ToList() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<KnowledgeEntry?> GetByExternalKeyAsync(
        Guid? projectId, string externalKey, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<KnowledgeRow>(
            projectId is null
                ? "SELECT * FROM knowledge_entries WHERE project_id IS NULL AND external_key=@key LIMIT 1;"
                : "SELECT * FROM knowledge_entries WHERE project_id=@project AND external_key=@key LIMIT 1;",
            new { project = projectId?.ToString(), key = externalKey }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> ListAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ps = new DynamicParameters();
        var where = BuildWhere(query, ps);
        var limit = Math.Clamp(query.Limit, 1, 500);
        var offset = Math.Max(0, query.Offset);
        ps.Add("limit", limit);
        ps.Add("offset", offset);
        var rows = await conn.QueryAsync<KnowledgeRow>(
            $@"SELECT * FROM knowledge_entries {WhereSql(where)}
               ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;", ps).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> SearchLexicalAsync(
        string query, KnowledgeQuery filter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return await ListAsync(filter, ct).ConfigureAwait(false);
        var term = query.Trim();

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        if (IsPostgres)
        {
            var ps = new DynamicParameters();
            var where = BuildWhere(filter, ps);
            where.Add("(title ILIKE @q OR content ILIKE @q)");
            ps.Add("q", $"%{term}%");
            ps.Add("limit", Math.Clamp(filter.Limit, 1, 500));
            ps.Add("offset", Math.Max(0, filter.Offset));
            var rows = await conn.QueryAsync<KnowledgeRow>(
                $@"SELECT * FROM knowledge_entries {WhereSql(where)}
                   ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;", ps).ConfigureAwait(false);
            return rows.Select(Map).ToList();
        }

        var ids = new List<string>();
        if (term.Length < 3)
        {
            var ps = new DynamicParameters();
            var where = BuildWhere(filter, ps);
            where.Add("(title LIKE @q OR content LIKE @q)");
            ps.Add("q", $"%{term}%");
            ps.Add("limit", Math.Clamp(filter.Limit, 1, 500));
            ps.Add("offset", Math.Max(0, filter.Offset));
            var rows = await conn.QueryAsync<KnowledgeRow>(
                $@"SELECT * FROM knowledge_entries {WhereSql(where)}
                   ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;", ps).ConfigureAwait(false);
            return rows.Select(Map).ToList();
        }

        try
        {
            var ftsIds = (await conn.QueryAsync<string>(
                "SELECT knowledge_id FROM knowledge_fts WHERE knowledge_fts MATCH @q ORDER BY rank LIMIT 500;",
                new { q = term }).ConfigureAwait(false)).ToList();
            ids = ftsIds;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            var rows = await conn.QueryAsync<string>(
                "SELECT id FROM knowledge_entries WHERE title LIKE @q OR content LIKE @q LIMIT 500;",
                new { q = $"%{term}%" }).ConfigureAwait(false);
            ids = rows.ToList();
        }

        if (ids.Count == 0) return Array.Empty<KnowledgeEntry>();
        var ps2 = new DynamicParameters();
        var where2 = BuildWhere(filter, ps2);
        where2.Add("id IN @ids");
        ps2.Add("ids", ids);
        var candidates = (await conn.QueryAsync<KnowledgeRow>(
            $"SELECT * FROM knowledge_entries {WhereSql(where2)};", ps2).ConfigureAwait(false)).ToList();

        var rank = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return candidates
            .OrderBy(r => rank.TryGetValue(r.id, out var i) ? i : int.MaxValue)
            .Select(Map)
            .Skip(Math.Max(0, filter.Offset))
            .Take(Math.Clamp(filter.Limit, 1, 500))
            .ToList();
    }

    public async Task<bool> UpdateAsync(KnowledgeEntry entry, int expectedVersion, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        var affected = await conn.ExecuteAsync(
            @"UPDATE knowledge_entries SET
                title=@title, content=@content, type=@type, project_id=@project,
                repository_id=@repo, status=@status, source=@source, source_ref=@sourceRef,
                external_key=@externalKey, content_hash=@contentHash, created_by=@createdBy,
                metadata=@metadata, updated_at=@updatedAt, valid_from=@validFrom,
                valid_until=@validUntil, version=@version, embedded_model=@embeddedModel,
                embedded_config_hash=@embeddedConfigHash, embedded_at=@embeddedAt
              WHERE id=@id AND version=@expectedVersion;",
            new
            {
                id = entry.Id.ToString(),
                title = entry.Title,
                content = entry.Content,
                type = Text(entry.Type),
                project = entry.ProjectId?.ToString(),
                repo = entry.RepositoryId?.ToString(),
                status = Text(entry.Status),
                source = entry.Source,
                sourceRef = entry.SourceRef,
                externalKey = entry.ExternalKey,
                contentHash = entry.ContentHash,
                createdBy = entry.CreatedBy,
                metadata = entry.Metadata,
                updatedAt = entry.UpdatedAt.ToString("o"),
                validFrom = entry.ValidFrom?.ToString("o"),
                validUntil = entry.ValidUntil?.ToString("o"),
                version = entry.Version,
                embeddedModel = entry.EmbeddedModel,
                embeddedConfigHash = entry.EmbeddedConfigHash,
                embeddedAt = entry.EmbeddedAt?.ToString("o"),
                expectedVersion
            }, tx).ConfigureAwait(false);

        if (affected > 0)
        {
            await UpsertFtsAsync(conn, tx, entry.Id, entry.Title, entry.Content).ConfigureAwait(false);
        }
        tx.Commit();
        return affected > 0;
    }

    public async Task<int> CountAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ps = new DynamicParameters();
        var where = BuildWhere(query, ps);
        return await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM knowledge_entries {WhereSql(where)};", ps).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> GetEmbeddingCandidatesAsync(
        string model, string configHash, int limit = 100, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<KnowledgeRow>(
            @"SELECT * FROM knowledge_entries
              WHERE status <> 'archived'
                AND (embedded_at IS NULL OR embedded_model IS NULL OR embedded_model <> @model
                     OR embedded_config_hash IS NULL OR embedded_config_hash <> @configHash)
              ORDER BY updated_at DESC LIMIT @limit;",
            new { model, configHash, limit = Math.Clamp(limit, 1, 1000) }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task MarkEmbeddedAsync(
        Guid id, string model, string configHash, DateTimeOffset at, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"UPDATE knowledge_entries
              SET embedded_model=@model, embedded_config_hash=@configHash, embedded_at=@at
              WHERE id=@id;",
            new { id = id.ToString(), model, configHash, at = at.ToString("o") }).ConfigureAwait(false);
    }

    private async Task UpsertFtsAsync(
        IDbConnection conn, IDbTransaction tx, Guid id, string title, string content)
    {
        var p = new { id = id.ToString(), title, content };
        if (IsPostgres)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO knowledge_fts(knowledge_id, title, content) VALUES(@id, @title, @content)
                  ON CONFLICT(knowledge_id) DO UPDATE SET title=excluded.title, content=excluded.content;",
                p, tx).ConfigureAwait(false);
            return;
        }
        await conn.ExecuteAsync("DELETE FROM knowledge_fts WHERE knowledge_id=@id;", p, tx).ConfigureAwait(false);
        await conn.ExecuteAsync(
            "INSERT INTO knowledge_fts(knowledge_id, title, content) VALUES(@id, @title, @content);",
            p, tx).ConfigureAwait(false);
    }

    private static List<string> BuildWhere(KnowledgeQuery query, DynamicParameters ps)
    {
        var where = new List<string>();
        if (!query.IncludeArchived)
        {
            where.Add("status <> 'archived'");
        }
        if (query.Statuses is { Count: > 0 })
        {
            where.Add("status IN @statuses");
            ps.Add("statuses", query.Statuses.Select(Text).ToList());
        }
        if (query.Types is { Count: > 0 })
        {
            where.Add("type IN @types");
            ps.Add("types", query.Types.Select(Text).ToList());
        }
        if (query.GlobalOnly)
        {
            where.Add("project_id IS NULL");
        }
        else if (query.ProjectId is not null)
        {
            where.Add("project_id = @project");
            ps.Add("project", query.ProjectId.Value.ToString());
        }
        if (query.RepositoryId is not null)
        {
            where.Add("repository_id = @repo");
            ps.Add("repo", query.RepositoryId.Value.ToString());
        }
        return where;
    }

    private static string WhereSql(List<string> where) => where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

    private static object Params(KnowledgeEntry e) => new
    {
        id = e.Id.ToString(),
        title = e.Title,
        content = e.Content,
        type = Text(e.Type),
        project = e.ProjectId?.ToString(),
        repo = e.RepositoryId?.ToString(),
        status = Text(e.Status),
        source = e.Source,
        sourceRef = e.SourceRef,
        externalKey = e.ExternalKey,
        contentHash = e.ContentHash,
        createdBy = e.CreatedBy,
        metadata = e.Metadata,
        createdAt = e.CreatedAt.ToString("o"),
        updatedAt = e.UpdatedAt.ToString("o"),
        validFrom = e.ValidFrom?.ToString("o"),
        validUntil = e.ValidUntil?.ToString("o"),
        version = e.Version,
        embeddedModel = e.EmbeddedModel,
        embeddedConfigHash = e.EmbeddedConfigHash,
        embeddedAt = e.EmbeddedAt?.ToString("o")
    };

    private const string InsertSql = @"INSERT INTO knowledge_entries(
        id, title, content, type, project_id, repository_id, status, source, source_ref,
        external_key, content_hash, created_by, metadata, created_at, updated_at,
        valid_from, valid_until, version, embedded_model, embedded_config_hash, embedded_at)
      VALUES(@id, @title, @content, @type, @project, @repo, @status, @source, @sourceRef,
        @externalKey, @contentHash, @createdBy, @metadata, @createdAt, @updatedAt,
        @validFrom, @validUntil, @version, @embeddedModel, @embeddedConfigHash, @embeddedAt);";

    private static KnowledgeEntry Map(KnowledgeRow r) => KnowledgeEntry.Rehydrate(
        Guid.Parse(r.id), r.title, r.content,
        TaskStore.Parse(r.type, KnowledgeType.Fact),
        TaskStore.GuidOrNull(r.project_id), TaskStore.GuidOrNull(r.repository_id),
        TaskStore.Parse(r.status, KnowledgeStatus.Active),
        r.source, r.source_ref, r.external_key, r.content_hash, r.created_by, r.metadata,
        DateTimeOffset.Parse(r.created_at), DateTimeOffset.Parse(r.updated_at),
        string.IsNullOrWhiteSpace(r.valid_from) ? null : DateTimeOffset.Parse(r.valid_from),
        string.IsNullOrWhiteSpace(r.valid_until) ? null : DateTimeOffset.Parse(r.valid_until),
        r.version, r.embedded_model, r.embedded_config_hash,
        string.IsNullOrWhiteSpace(r.embedded_at) ? null : DateTimeOffset.Parse(r.embedded_at));

    internal static string Text<TEnum>(TEnum value) where TEnum : struct, Enum
        => value.ToString().ToLowerInvariant();

    private sealed class KnowledgeRow
    {
        public string id { get; set; } = "";
        public string title { get; set; } = "";
        public string content { get; set; } = "";
        public string type { get; set; } = "";
        public string? project_id { get; set; }
        public string? repository_id { get; set; }
        public string status { get; set; } = "";
        public string source { get; set; } = "";
        public string? source_ref { get; set; }
        public string? external_key { get; set; }
        public string content_hash { get; set; } = "";
        public string? created_by { get; set; }
        public string? metadata { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string? valid_from { get; set; }
        public string? valid_until { get; set; }
        public int version { get; set; }
        public string? embedded_model { get; set; }
        public string? embedded_config_hash { get; set; }
        public string? embedded_at { get; set; }
    }
}
