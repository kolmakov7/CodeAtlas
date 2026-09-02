using System.Data;
using System.Text.Json;
using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence.Repositories;

/// <summary>
/// Maintains the precomputed statistics snapshots (repository_stats / global_stats).
/// Runs off the read path: startup (EnsureAsync) and after indexing jobs complete.
/// </summary>
public sealed class DapperStatisticsRefresher : IStatisticsRefresher
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DapperStatisticsRefresher> _logger;

    private static readonly string[] DomainTokens = { "code", "text" };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public DapperStatisticsRefresher(IDbConnectionFactory factory, ILogger<DapperStatisticsRefresher> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task RefreshRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var rid = repositoryId.ToString();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var name = await conn.ExecuteScalarAsync<string?>("SELECT name FROM repositories WHERE id=@id;", new { id = rid }).ConfigureAwait(false);
        if (name is null) return; // repository already deleted

        var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var chunkCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        long vectorCount = 0;
        try
        {
            vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id=@rid);", new { rid }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "vector_entries count failed for {Repo}", repositoryId);
        }

        var embedding = new Dictionary<string, (long Indexed, long Pending)>();
        foreach (var domain in DomainTokens)
            embedding[domain] = await GetDomainStatsAsync(conn, rid, domain, ct).ConfigureAwait(false);

        var failed = await GetFailedJobsAsync(conn, rid, ct).ConfigureAwait(false);
        var (relationCount, relationsByType) = await GetRelationStatsAsync(conn, rid, ct).ConfigureAwait(false);
        var languages = await GetLanguageBreakdownAsync(conn, rid, ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO repository_stats(
                repository_id, file_count, symbol_count, chunk_count, vector_count,
                relation_count, relations_by_type, language_breakdown,
                embedding_code_indexed, embedding_code_pending,
                embedding_text_indexed, embedding_text_pending,
                embeddings_failed, updated_at
            ) VALUES (
                @rid, @fc, @sc, @cc, @vc, @rc, @rbt, @lb,
                @codeIndexed, @codePending, @textIndexed, @textPending, @failed, @now
            )
            ON CONFLICT(repository_id) DO UPDATE SET
                file_count = excluded.file_count,
                symbol_count = excluded.symbol_count,
                chunk_count = excluded.chunk_count,
                vector_count = excluded.vector_count,
                relation_count = excluded.relation_count,
                relations_by_type = excluded.relations_by_type,
                language_breakdown = excluded.language_breakdown,
                embedding_code_indexed = excluded.embedding_code_indexed,
                embedding_code_pending = excluded.embedding_code_pending,
                embedding_text_indexed = excluded.embedding_text_indexed,
                embedding_text_pending = excluded.embedding_text_pending,
                embeddings_failed = excluded.embeddings_failed,
                updated_at = excluded.updated_at;
            """,
            new
            {
                rid,
                fc = (long)fileCount,
                sc = (long)symbolCount,
                cc = (long)chunkCount,
                vc = vectorCount,
                rc = (long)relationCount,
                rbt = JsonSerializer.Serialize(relationsByType, JsonOpts),
                lb = JsonSerializer.Serialize(languages, JsonOpts),
                codeIndexed = embedding["code"].Indexed,
                codePending = embedding["code"].Pending,
                textIndexed = embedding["text"].Indexed,
                textPending = embedding["text"].Pending,
                failed = (long)failed,
                now = DateTimeOffset.UtcNow.ToString("o")
            }).ConfigureAwait(false);

        _logger.LogDebug("Statistics snapshot refreshed for repository {Repo}", repositoryId);
    }

    public async Task RefreshGlobalAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        // Aggregate from per-repo snapshots (repos without a snapshot count as empty).
        var row = await conn.QueryFirstAsync<GlobalRow>(
            """
            SELECT
                (SELECT COUNT(*) FROM repositories) AS repository_count,
                COALESCE((SELECT SUM(file_count) FROM repository_stats), 0) AS file_count,
                COALESCE((SELECT SUM(symbol_count) FROM repository_stats), 0) AS symbol_count,
                COALESCE((SELECT SUM(chunk_count) FROM repository_stats), 0) AS chunk_count,
                COALESCE((SELECT SUM(relation_count) FROM repository_stats), 0) AS relation_count,
                COALESCE((SELECT SUM(embedding_code_indexed) FROM repository_stats), 0) AS embedding_code_indexed,
                COALESCE((SELECT SUM(embedding_code_pending) FROM repository_stats), 0) AS embedding_code_pending,
                COALESCE((SELECT SUM(embedding_text_indexed) FROM repository_stats), 0) AS embedding_text_indexed,
                COALESCE((SELECT SUM(embedding_text_pending) FROM repository_stats), 0) AS embedding_text_pending,
                COALESCE((SELECT SUM(embeddings_failed) FROM repository_stats), 0) AS embeddings_failed;
            """).ConfigureAwait(false);

        var jobCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM indexing_jobs;").ConfigureAwait(false);
        var (relationsByType, _) = await GetRelationStatsAsync(conn, null, ct).ConfigureAwait(false);
        var languages = await GetLanguageBreakdownAsync(conn, null, ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO global_stats(
                id, repository_count, file_count, symbol_count, chunk_count, relation_count,
                relations_by_type, language_breakdown,
                embedding_code_indexed, embedding_code_pending,
                embedding_text_indexed, embedding_text_pending,
                embeddings_failed, job_count, updated_at
            ) VALUES (1, @repositoryCount, @fileCount, @symbolCount, @chunkCount, @relationCount,
                @rbt, @lb, @codeIndexed, @codePending, @textIndexed, @textPending, @failed, @jobs, @now)
            ON CONFLICT(id) DO UPDATE SET
                repository_count = excluded.repository_count,
                file_count = excluded.file_count,
                symbol_count = excluded.symbol_count,
                chunk_count = excluded.chunk_count,
                relation_count = excluded.relation_count,
                relations_by_type = excluded.relations_by_type,
                language_breakdown = excluded.language_breakdown,
                embedding_code_indexed = excluded.embedding_code_indexed,
                embedding_code_pending = excluded.embedding_code_pending,
                embedding_text_indexed = excluded.embedding_text_indexed,
                embedding_text_pending = excluded.embedding_text_pending,
                embeddings_failed = excluded.embeddings_failed,
                job_count = excluded.job_count,
                updated_at = excluded.updated_at;
            """,
            new
            {
                repositoryCount = row.repository_count,
                fileCount = row.file_count,
                symbolCount = row.symbol_count,
                chunkCount = row.chunk_count,
                relationCount = row.relation_count,
                codeIndexed = row.embedding_code_indexed,
                codePending = row.embedding_code_pending,
                textIndexed = row.embedding_text_indexed,
                textPending = row.embedding_text_pending,
                failed = row.embeddings_failed,
                rbt = JsonSerializer.Serialize(relationsByType, JsonOpts),
                lb = JsonSerializer.Serialize(languages, JsonOpts),
                jobs = jobCount,
                now = DateTimeOffset.UtcNow.ToString("o")
            }).ConfigureAwait(false);

        _logger.LogDebug("Global statistics snapshot refreshed ({Repos} repos)", row.repository_count);
    }

    public async Task EnsureAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var hasGlobal = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM global_stats;").ConfigureAwait(false);
        if (hasGlobal > 0) return;

        var repoIds = (await conn.QueryAsync<string>("SELECT id FROM repositories;").ConfigureAwait(false)).ToArray();
        _logger.LogInformation("Statistics snapshots are empty — refreshing {Count} repositories + global", repoIds.Length);
        foreach (var id in repoIds)
        {
            try { await RefreshRepositoryAsync(Guid.Parse(id), ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to refresh statistics for repository {Repo}", id); }
        }
        await RefreshGlobalAsync(ct).ConfigureAwait(false);
    }

    private async Task<(long Indexed, long Pending)> GetDomainStatsAsync(IDbConnection conn, string? rid, string domain, CancellationToken ct)
    {
        try
        {
            long total, indexed;
            if (rid is null)
            {
                total = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE embedding_domain=@domain;", new { domain }).ConfigureAwait(false);
                indexed = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(DISTINCT ve.chunk_id) FROM vector_entries ve INNER JOIN chunks c ON c.id = ve.chunk_id WHERE c.embedding_domain=@domain AND ve.domain=@domain;",
                    new { domain }).ConfigureAwait(false);
            }
            else
            {
                total = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid AND embedding_domain=@domain;", new { rid, domain }).ConfigureAwait(false);
                indexed = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(DISTINCT ve.chunk_id) FROM vector_entries ve INNER JOIN chunks c ON c.id = ve.chunk_id WHERE c.repository_id=@rid AND c.embedding_domain=@domain AND ve.domain=@domain;",
                    new { rid, domain }).ConfigureAwait(false);
            }
            return (indexed, Math.Max(0, total - indexed));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Per-domain embedding stats failed for {Domain}", domain);
            return (0, 0);
        }
    }

    private async Task<long> GetFailedJobsAsync(IDbConnection conn, string? rid, CancellationToken ct)
    {
        try
        {
            if (rid is null)
                return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM indexing_jobs WHERE status='Failed';").ConfigureAwait(false);
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM indexing_jobs WHERE repository_id=@rid AND status='Failed';", new { rid }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed-jobs count query failed");
            return 0;
        }
    }

    private async Task<(int Count, IReadOnlyList<RelationTypeStats> ByType)> GetRelationStatsAsync(IDbConnection conn, string? rid, CancellationToken ct)
    {
        int count = 0;
        try
        {
            count = rid is null
                ? await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM relations;").ConfigureAwait(false)
                : await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM relations WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relation count query failed for {Repo}", rid ?? "global");
            if (rid is not null)
            {
                try
                {
                    count = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM relations r WHERE EXISTS (SELECT 1 FROM symbols s WHERE s.id = r.from_symbol_id AND s.repository_id=@rid);",
                        new { rid }).ConfigureAwait(false);
                }
                catch { }
            }
        }

        var byType = new List<RelationTypeStats>();
        try
        {
            var rows = rid is null
                ? await conn.QueryAsync<RelTypeRow>("SELECT relation_type, COUNT(*) AS cnt FROM relations GROUP BY relation_type ORDER BY cnt DESC;").ConfigureAwait(false)
                : await conn.QueryAsync<RelTypeRow>("SELECT relation_type, COUNT(*) AS cnt FROM relations WHERE repository_id=@rid GROUP BY relation_type ORDER BY cnt DESC;", new { rid }).ConfigureAwait(false);
            byType.AddRange(rows.Select(r => new RelationTypeStats(r.relation_type, r.cnt)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relation by-type query failed for {Repo}", rid ?? "global");
        }

        return (count, byType);
    }

    private async Task<Dictionary<string, int>> GetLanguageBreakdownAsync(IDbConnection conn, string? rid, CancellationToken ct)
    {
        var result = new Dictionary<string, int>();
        try
        {
            var rows = rid is null
                ? await conn.QueryAsync<LangRow>("SELECT language, COUNT(*) AS cnt FROM files WHERE language IS NOT NULL AND language != '' GROUP BY language;").ConfigureAwait(false)
                : await conn.QueryAsync<LangRow>("SELECT language, COUNT(*) AS cnt FROM files WHERE repository_id=@rid AND language IS NOT NULL AND language != '' GROUP BY language;", new { rid }).ConfigureAwait(false);
            foreach (var r in rows)
                result[r.language] = r.cnt;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Language breakdown query failed for {Repo}", rid ?? "global");
        }
        return result;
    }

    private sealed class GlobalRow
    {
        public long repository_count { get; set; }
        public long file_count { get; set; }
        public long symbol_count { get; set; }
        public long chunk_count { get; set; }
        public long relation_count { get; set; }
        public long embedding_code_indexed { get; set; }
        public long embedding_code_pending { get; set; }
        public long embedding_text_indexed { get; set; }
        public long embedding_text_pending { get; set; }
        public long embeddings_failed { get; set; }
    }

    private sealed class RelTypeRow
    {
        public string relation_type { get; set; } = "";
        public int cnt { get; set; }
    }

    private sealed class LangRow
    {
        public string language { get; set; } = "";
        public int cnt { get; set; }
    }
}
