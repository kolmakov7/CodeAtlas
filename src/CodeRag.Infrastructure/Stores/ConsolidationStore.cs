using System.Data;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class ConsolidationStore : IConsolidationStore
{
    private readonly IDbConnectionFactory _factory;

    public ConsolidationStore(IDbConnectionFactory factory) => _factory = factory;

    private bool IsPostgres => SqlDialect.IsPostgres(_factory);

    public async Task<bool> CreateIfAbsentAsync(
        ConsolidationJob job,
        IReadOnlyList<ConsolidationJobItem> items,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        var inserted = await conn.ExecuteAsync(
            @"INSERT INTO consolidation_jobs(
                id, type, status, reason, project_id, repository_id, dedupe_key, input_version,
                attempts, last_error, analysis, proposal, proposal_version, created_at, updated_at,
                resolved_at, resolved_by, result_knowledge_id, version)
              VALUES(@id, @type, @status, @reason, @project, @repo, @dedupeKey, @inputVersion,
                @attempts, @lastError, @analysis, @proposal, @proposalVersion, @createdAt, @updatedAt,
                @resolvedAt, @resolvedBy, @resultKnowledgeId, @version)
              ON CONFLICT(dedupe_key) DO NOTHING;",
            Params(job), tx).ConfigureAwait(false);
        if (inserted == 0)
        {
            tx.Rollback();
            return false;
        }
        foreach (var item in items)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO consolidation_job_items(job_id, knowledge_id, role, version_at_creation, content_hash_at_creation, previous_status)
                  VALUES(@job, @knowledge, @role, @version, @hash, @previousStatus)
                  ON CONFLICT(job_id, knowledge_id) DO NOTHING;",
                new
                {
                    job = item.JobId.ToString(),
                    knowledge = item.KnowledgeId.ToString(),
                    role = item.Role,
                    version = item.VersionAtCreation,
                    hash = item.ContentHashAtCreation,
                    previousStatus = item.PreviousStatus
                }, tx).ConfigureAwait(false);
        }
        tx.Commit();
        return true;
    }

    public async Task<ConsolidationJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<JobRow>(
            "SELECT * FROM consolidation_jobs WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<ConsolidationJob?> GetByDedupeKeyAsync(string dedupeKey, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<JobRow>(
            "SELECT * FROM consolidation_jobs WHERE dedupe_key=@key;", new { key = dedupeKey }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<ConsolidationJob>> ListAsync(
        ConsolidationJobQuery query, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ps = new DynamicParameters();
        var where = new List<string>();
        if (query.Status is not null)
        {
            where.Add("status=@status");
            ps.Add("status", TaskStore.Text(query.Status.Value));
        }
        if (query.Type is not null)
        {
            where.Add("type=@type");
            ps.Add("type", TaskStore.Text(query.Type.Value));
        }
        if (query.ProjectId is not null)
        {
            where.Add("project_id=@project");
            ps.Add("project", query.ProjectId.Value.ToString());
        }
        if (query.RepositoryId is not null)
        {
            where.Add("repository_id=@repo");
            ps.Add("repo", query.RepositoryId.Value.ToString());
        }
        ps.Add("limit", Math.Clamp(query.Limit, 1, 500));
        ps.Add("offset", Math.Max(0, query.Offset));
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var rows = await conn.QueryAsync<JobRow>(
            $@"SELECT * FROM consolidation_jobs {whereSql}
               ORDER BY updated_at DESC LIMIT @limit OFFSET @offset;", ps).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<ConsolidationJobItem>> GetItemsAsync(Guid jobId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ItemRow>(
            "SELECT * FROM consolidation_job_items WHERE job_id=@job;",
            new { job = jobId.ToString() }).ConfigureAwait(false);
        return rows.Select(r => ConsolidationJobItem.Rehydrate(
            Guid.Parse(r.job_id), Guid.Parse(r.knowledge_id), r.role, r.version_at_creation,
            r.content_hash_at_creation, r.previous_status)).ToList();
    }

    public async Task<int> CountAsync(ConsolidationJobQuery query, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ps = new DynamicParameters();
        var where = new List<string>();
        if (query.Status is not null)
        {
            where.Add("status=@status");
            ps.Add("status", TaskStore.Text(query.Status.Value));
        }
        if (query.Type is not null)
        {
            where.Add("type=@type");
            ps.Add("type", TaskStore.Text(query.Type.Value));
        }
        if (query.ProjectId is not null)
        {
            where.Add("project_id=@project");
            ps.Add("project", query.ProjectId.Value.ToString());
        }
        if (query.RepositoryId is not null)
        {
            where.Add("repository_id=@repo");
            ps.Add("repo", query.RepositoryId.Value.ToString());
        }
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        return await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM consolidation_jobs {whereSql};", ps).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(ConsolidationJob job, int expectedVersion, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var affected = await conn.ExecuteAsync(
            @"UPDATE consolidation_jobs SET
                status=@status, attempts=@attempts, last_error=@lastError, analysis=@analysis,
                proposal=@proposal, proposal_version=@proposalVersion, input_version=@inputVersion,
                updated_at=@updatedAt, resolved_at=@resolvedAt, resolved_by=@resolvedBy,
                result_knowledge_id=@resultKnowledgeId, version=@version
              WHERE id=@id AND version=@expectedVersion;",
            new
            {
                id = job.Id.ToString(),
                status = TaskStore.Text(job.Status),
                attempts = job.Attempts,
                lastError = job.LastError,
                analysis = job.Analysis,
                proposal = job.Proposal,
                proposalVersion = job.ProposalVersion,
                inputVersion = job.InputVersion,
                updatedAt = job.UpdatedAt.ToString("o"),
                resolvedAt = job.ResolvedAt?.ToString("o"),
                resolvedBy = job.ResolvedBy,
                resultKnowledgeId = job.ResultKnowledgeId?.ToString(),
                version = job.Version,
                expectedVersion
            }).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<ConsolidationApplyResult> ApplyAsync(
        ConsolidationJob job,
        IReadOnlyList<ConsolidationJobItem> items,
        KnowledgeEntry merged,
        int expectedJobVersion,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        foreach (var item in items)
        {
            var affected = await conn.ExecuteAsync(
                @"UPDATE knowledge_entries
                  SET status='superseded', version=version+1, updated_at=@now
                  WHERE id=@id AND version=@version AND content_hash=@hash;",
                new
                {
                    id = item.KnowledgeId.ToString(),
                    version = item.VersionAtCreation,
                    hash = item.ContentHashAtCreation,
                    now = now.ToString("o")
                }, tx).ConfigureAwait(false);
            if (affected == 0)
            {
                tx.Rollback();
                return new ConsolidationApplyResult(false,
                    $"Knowledge {item.KnowledgeId} changed since the proposal was generated.");
            }
        }

        await conn.ExecuteAsync(KnowledgeInsertSql, KnowledgeParams(merged), tx).ConfigureAwait(false);
        await UpsertFtsAsync(conn, tx, merged.Id, merged.Title, merged.Content).ConfigureAwait(false);

        foreach (var item in items)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO entity_links(id, from_type, from_id, to_type, to_id, relation, note, created_by, created_at)
                  VALUES(@id, 'knowledge', @fromId, 'knowledge', @toId, 'derived_from', NULL, @actor, @at)
                  ON CONFLICT(from_type, from_id, to_type, to_id, relation) DO NOTHING;",
                new
                {
                    id = Guid.NewGuid().ToString(),
                    fromId = merged.Id.ToString(),
                    toId = item.KnowledgeId.ToString(),
                    actor = job.ResolvedBy,
                    at = now.ToString("o")
                }, tx).ConfigureAwait(false);

            await conn.ExecuteAsync(HistoryInsertSql, new
            {
                entityType = EntityTypes.Knowledge,
                entityId = item.KnowledgeId.ToString(),
                at = now.ToString("o"),
                actor = job.ResolvedBy,
                action = "superseded_by_consolidation",
                versionFrom = item.VersionAtCreation,
                versionTo = item.VersionAtCreation + 1,
                changes = $"{{\"jobId\":\"{job.Id}\",\"resultId\":\"{merged.Id}\"}}"
            }, tx).ConfigureAwait(false);
        }

        await conn.ExecuteAsync(HistoryInsertSql, new
        {
            entityType = EntityTypes.Knowledge,
            entityId = merged.Id.ToString(),
            at = now.ToString("o"),
            actor = job.ResolvedBy,
            action = "created_by_consolidation",
            versionFrom = (int?)null,
            versionTo = merged.Version,
            changes = $"{{\"jobId\":\"{job.Id}\"}}"
        }, tx).ConfigureAwait(false);

        var jobUpdated = await conn.ExecuteAsync(
            @"UPDATE consolidation_jobs SET
                status=@status, proposal=@proposal, proposal_version=@proposalVersion,
                input_version=@inputVersion, updated_at=@updatedAt, resolved_at=@resolvedAt,
                resolved_by=@resolvedBy, result_knowledge_id=@resultKnowledgeId, version=@version
              WHERE id=@id AND version=@expectedVersion;",
            new
            {
                id = job.Id.ToString(),
                status = TaskStore.Text(job.Status),
                proposal = job.Proposal,
                proposalVersion = job.ProposalVersion,
                inputVersion = job.InputVersion,
                updatedAt = now.ToString("o"),
                resolvedAt = now.ToString("o"),
                resolvedBy = job.ResolvedBy,
                resultKnowledgeId = merged.Id.ToString(),
                version = job.Version,
                expectedVersion = expectedJobVersion
            }, tx).ConfigureAwait(false);

        if (jobUpdated == 0)
        {
            tx.Rollback();
            return new ConsolidationApplyResult(false, "Consolidation job changed since it was read.");
        }

        tx.Commit();
        return new ConsolidationApplyResult(true, null, merged.Id);
    }

    public async Task<ConsolidationApplyResult> UndoAsync(
        ConsolidationJob job,
        IReadOnlyList<ConsolidationJobItem> items,
        Guid resultKnowledgeId,
        int expectedJobVersion,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        var restored = 0;

        foreach (var item in items)
        {
            var affected = await conn.ExecuteAsync(
                @"UPDATE knowledge_entries
                  SET status=@previous, version=version+1, updated_at=@now
                  WHERE id=@id AND status='superseded';",
                new
                {
                    id = item.KnowledgeId.ToString(),
                    previous = item.PreviousStatus,
                    now = now.ToString("o")
                }, tx).ConfigureAwait(false);
            if (affected == 0) continue;
            restored++;
            await conn.ExecuteAsync(HistoryInsertSql, new
            {
                entityType = EntityTypes.Knowledge,
                entityId = item.KnowledgeId.ToString(),
                at = now.ToString("o"),
                actor = job.ResolvedBy,
                action = "restored_by_consolidation_undo",
                versionFrom = (int?)null,
                versionTo = (int?)null,
                changes = $"{{\"jobId\":\"{job.Id}\",\"resultId\":\"{resultKnowledgeId}\"}}"
            }, tx).ConfigureAwait(false);
        }

        await conn.ExecuteAsync(
            @"UPDATE knowledge_entries
              SET status='archived', version=version+1, updated_at=@now
              WHERE id=@id AND status <> 'archived';",
            new { id = resultKnowledgeId.ToString(), now = now.ToString("o") }, tx).ConfigureAwait(false);

        await conn.ExecuteAsync(HistoryInsertSql, new
        {
            entityType = EntityTypes.Knowledge,
            entityId = resultKnowledgeId.ToString(),
            at = now.ToString("o"),
            actor = job.ResolvedBy,
            action = "archived_by_consolidation_undo",
            versionFrom = (int?)null,
            versionTo = (int?)null,
            changes = $"{{\"jobId\":\"{job.Id}\"}}"
        }, tx).ConfigureAwait(false);

        var jobUpdated = await conn.ExecuteAsync(
            @"UPDATE consolidation_jobs SET
                status=@status, updated_at=@updatedAt, resolved_at=@resolvedAt,
                resolved_by=@resolvedBy, version=@version
              WHERE id=@id AND version=@expectedVersion;",
            new
            {
                id = job.Id.ToString(),
                status = TaskStore.Text(job.Status),
                updatedAt = now.ToString("o"),
                resolvedAt = now.ToString("o"),
                resolvedBy = job.ResolvedBy,
                version = job.Version,
                expectedVersion = expectedJobVersion
            }, tx).ConfigureAwait(false);

        if (jobUpdated == 0)
        {
            tx.Rollback();
            return new ConsolidationApplyResult(false, "Consolidation job changed since it was read.", resultKnowledgeId);
        }

        tx.Commit();
        return new ConsolidationApplyResult(true, null, resultKnowledgeId, restored);
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

    private static object Params(ConsolidationJob j) => new
    {
        id = j.Id.ToString(),
        type = TaskStore.Text(j.Type),
        status = TaskStore.Text(j.Status),
        reason = j.Reason,
        project = j.ProjectId?.ToString(),
        repo = j.RepositoryId?.ToString(),
        dedupeKey = j.DedupeKey,
        inputVersion = j.InputVersion,
        attempts = j.Attempts,
        lastError = j.LastError,
        analysis = j.Analysis,
        proposal = j.Proposal,
        proposalVersion = j.ProposalVersion,
        createdAt = j.CreatedAt.ToString("o"),
        updatedAt = j.UpdatedAt.ToString("o"),
        resolvedAt = j.ResolvedAt?.ToString("o"),
        resolvedBy = j.ResolvedBy,
        resultKnowledgeId = j.ResultKnowledgeId?.ToString(),
        version = j.Version
    };

    private static object KnowledgeParams(KnowledgeEntry e) => new
    {
        id = e.Id.ToString(),
        title = e.Title,
        content = e.Content,
        type = TaskStore.Text(e.Type),
        project = e.ProjectId?.ToString(),
        repo = e.RepositoryId?.ToString(),
        status = TaskStore.Text(e.Status),
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

    private const string KnowledgeInsertSql = @"INSERT INTO knowledge_entries(
        id, title, content, type, project_id, repository_id, status, source, source_ref,
        external_key, content_hash, created_by, metadata, created_at, updated_at,
        valid_from, valid_until, version, embedded_model, embedded_config_hash, embedded_at)
      VALUES(@id, @title, @content, @type, @project, @repo, @status, @source, @sourceRef,
        @externalKey, @contentHash, @createdBy, @metadata, @createdAt, @updatedAt,
        @validFrom, @validUntil, @version, @embeddedModel, @embeddedConfigHash, @embeddedAt);";

    private const string HistoryInsertSql = @"INSERT INTO entity_history(entity_type, entity_id, at, actor, action, version_from, version_to, changes)
      VALUES(@entityType, @entityId, @at, @actor, @action, @versionFrom, @versionTo, @changes);";

    private static ConsolidationJob Map(JobRow r) => ConsolidationJob.Rehydrate(
        Guid.Parse(r.id),
        TaskStore.Parse(r.type, ConsolidationJobType.Duplicates),
        TaskStore.Parse(r.status, ConsolidationJobStatus.Pending),
        r.reason,
        TaskStore.GuidOrNull(r.project_id),
        TaskStore.GuidOrNull(r.repository_id),
        r.dedupe_key,
        r.input_version,
        r.attempts,
        r.last_error,
        r.analysis,
        r.proposal,
        r.proposal_version,
        DateTimeOffset.Parse(r.created_at),
        DateTimeOffset.Parse(r.updated_at),
        string.IsNullOrWhiteSpace(r.resolved_at) ? null : DateTimeOffset.Parse(r.resolved_at),
        r.resolved_by,
        r.version,
        TaskStore.GuidOrNull(r.result_knowledge_id));

    private sealed class JobRow
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public string status { get; set; } = "";
        public string reason { get; set; } = "";
        public string? project_id { get; set; }
        public string? repository_id { get; set; }
        public string dedupe_key { get; set; } = "";
        public string input_version { get; set; } = "";
        public int attempts { get; set; }
        public string? last_error { get; set; }
        public string? analysis { get; set; }
        public string? proposal { get; set; }
        public int proposal_version { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string? resolved_at { get; set; }
        public string? resolved_by { get; set; }
        public string? result_knowledge_id { get; set; }
        public int version { get; set; }
    }

    private sealed class ItemRow
    {
        public string job_id { get; set; } = "";
        public string knowledge_id { get; set; } = "";
        public string role { get; set; } = "";
        public int version_at_creation { get; set; }
        public string content_hash_at_creation { get; set; } = "";
        public string previous_status { get; set; } = "active";
    }
}
