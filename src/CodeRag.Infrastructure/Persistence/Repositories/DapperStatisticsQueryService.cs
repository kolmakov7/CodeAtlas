using System.Data;
using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence.Repositories;

public sealed class DapperStatisticsQueryService : IStatisticsQueryService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DapperStatisticsQueryService> _logger;
    public DapperStatisticsQueryService(IDbConnectionFactory factory, ILogger<DapperStatisticsQueryService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    private static readonly string[] DomainTokens = { "code", "text" };

    public async Task<RepositoryStatistics> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var repoRow = await conn.QuerySingleOrDefaultAsync<RepoRow>("SELECT * FROM repositories WHERE id=@id;", new { id = repositoryId.ToString() }).ConfigureAwait(false);
        if (repoRow is null) throw new KeyNotFoundException($"Repository {repositoryId} not found.");
        var rid = repositoryId.ToString();
        var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var chunkCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        long vectorCount = 0;
        try { vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id=@rid);", new { rid }).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "vector_entries count failed for {Repo}", repositoryId); }

        var embeddingStats = new List<EmbeddingDomainStats>();
        foreach (var domain in DomainTokens)
            embeddingStats.Add(await GetDomainStatsAsync(conn, rid, domain, ct).ConfigureAwait(false));
        var failed = await GetFailedJobsAsync(conn, rid, ct).ConfigureAwait(false);
        var (relationCount, relationsByType) = await GetRelationStatsAsync(conn, rid, ct).ConfigureAwait(false);

        return new RepositoryStatistics(repositoryId, repoRow.name, (int)fileCount, (int)symbolCount, (int)chunkCount, (int)vectorCount,
            relationCount, relationsByType, embeddingStats, failed, repoRow.indexed_commit, repoRow.current_commit, repoRow.status);
    }

    public async Task<GlobalStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var repoRows = await conn.QueryAsync<RepoRow>("SELECT * FROM repositories ORDER BY name;").ConfigureAwait(false);
        var repoStats = new List<RepositoryStatistics>();
        foreach (var r in repoRows)
        {
            var rid = r.id;
            var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var chunkCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            long vectorCount = 0;
            try { vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id=@rid);", new { rid }).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "vector_entries count failed for {Repo}", rid); }

            var embeddingStats = new List<EmbeddingDomainStats>();
            foreach (var domain in DomainTokens)
                embeddingStats.Add(await GetDomainStatsAsync(conn, rid, domain, ct).ConfigureAwait(false));
            var failed = await GetFailedJobsAsync(conn, rid, ct).ConfigureAwait(false);
            var (relationCount, relationsByType) = await GetRelationStatsAsync(conn, rid, ct).ConfigureAwait(false);

            repoStats.Add(new RepositoryStatistics(Guid.Parse(r.id), r.name, (int)fileCount, (int)symbolCount, (int)chunkCount, (int)vectorCount, relationCount, relationsByType, embeddingStats, failed, r.indexed_commit, r.current_commit, r.status));
        }
        var totalFiles = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files;").ConfigureAwait(false);
        var totalSymbols = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols;").ConfigureAwait(false);
        var totalChunks = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks;").ConfigureAwait(false);
        var totalJobs = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM indexing_jobs;").ConfigureAwait(false);
        var (totalRelations, globalRelationsByType) = await GetRelationStatsAsync(conn, null, ct).ConfigureAwait(false);

        var globalEmbeddingStats = new List<EmbeddingDomainStats>();
        foreach (var domain in DomainTokens)
            globalEmbeddingStats.Add(await GetDomainStatsAsync(conn, null, domain, ct).ConfigureAwait(false));
        var globalFailed = await GetFailedJobsAsync(conn, null, ct).ConfigureAwait(false);

        return new GlobalStatistics(repoStats.Count, (int)totalFiles, (int)totalSymbols, (int)totalChunks, totalRelations, globalRelationsByType, (int)totalJobs, globalEmbeddingStats, globalFailed, repoStats);
    }

    /// <summary>Per-domain embedding progress (indexed = has vector, pending = no vector) for a repo (or null = global).</summary>
    private async Task<EmbeddingDomainStats> GetDomainStatsAsync(IDbConnection conn, string? rid, string domain, CancellationToken ct)
    {
        int total, indexed;
        try
        {
            if (rid is null)
            {
                total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chunks WHERE embedding_domain=@domain;", new { domain }).ConfigureAwait(false);
                indexed = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(DISTINCT ve.chunk_id) FROM vector_entries ve INNER JOIN chunks c ON c.id = ve.chunk_id WHERE c.embedding_domain=@domain AND ve.domain=@domain;",
                    new { domain }).ConfigureAwait(false);
            }
            else
            {
                total = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid AND embedding_domain=@domain;", new { rid, domain }).ConfigureAwait(false);
                indexed = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(DISTINCT ve.chunk_id) FROM vector_entries ve INNER JOIN chunks c ON c.id = ve.chunk_id WHERE c.repository_id=@rid AND c.embedding_domain=@domain AND ve.domain=@domain;",
                    new { rid, domain }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Per-domain embedding stats failed for {Domain}", domain);
            return new EmbeddingDomainStats(domain, 0, 0);
        }
        return new EmbeddingDomainStats(domain, indexed, Math.Max(0, total - indexed));
    }

    private async Task<int> GetFailedJobsAsync(IDbConnection conn, string? rid, CancellationToken ct)
    {
        try
        {
            if (rid is null)
                return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM indexing_jobs WHERE status='Failed';").ConfigureAwait(false);
            return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM indexing_jobs WHERE repository_id=@rid AND status='Failed';", new { rid }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed-jobs count query failed");
            return 0;
        }
    }

    /// <summary>Total relations and per-type breakdown for a repo (or null = global).</summary>
    private async Task<(int Count, IReadOnlyList<RelationTypeStats> ByType)> GetRelationStatsAsync(IDbConnection conn, string? rid, CancellationToken ct)
    {
        int count;
        try
        {
            if (rid is null)
                count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM relations;").ConfigureAwait(false);
            else
                count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM relations WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relation count query failed for {Repo}", rid ?? "global");
            count = 0;
        }

        var byType = new List<RelationTypeStats>();
        try
        {
            if (rid is null)
            {
                var rows = await conn.QueryAsync<RelTypeRow>("SELECT relation_type, COUNT(*) AS cnt FROM relations GROUP BY relation_type ORDER BY cnt DESC;").ConfigureAwait(false);
                byType.AddRange(rows.Select(r => new RelationTypeStats(r.relation_type, r.cnt)));
            }
            else
            {
                var rows = await conn.QueryAsync<RelTypeRow>("SELECT relation_type, COUNT(*) AS cnt FROM relations WHERE repository_id=@rid GROUP BY relation_type ORDER BY cnt DESC;", new { rid }).ConfigureAwait(false);
                byType.AddRange(rows.Select(r => new RelationTypeStats(r.relation_type, r.cnt)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Relation by-type query failed for {Repo}", rid ?? "global");
        }
        return (count, byType);
    }

    public Task<StorageSizes> GetStorageSizesAsync(CancellationToken ct = default)
    {
        long sqliteBytes = 0, vectorBytes = 0, reposBytes = 0;
        try { var fi = new FileInfo(_factory.DbPath); if (fi.Exists) sqliteBytes = fi.Length; } catch { }
        try { var vecPath = _factory.DbPath + "-vec"; if (File.Exists(vecPath)) vectorBytes = new FileInfo(vecPath).Length; } catch { }
        try
        {
            var reposRoot = Path.Combine(Path.GetDirectoryName(_factory.DbPath) ?? "/data", "repos");
            if (Directory.Exists(reposRoot))
                reposBytes = Directory.GetFiles(reposRoot, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
        } catch { }
        return Task.FromResult(new StorageSizes(sqliteBytes, vectorBytes, reposBytes));
    }

    private sealed class RepoRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string status { get; set; } = "";
        public string? current_commit { get; set; }
        public string? indexed_commit { get; set; }
    }

    private sealed class RelTypeRow
    {
        public string relation_type { get; set; } = "";
        public int cnt { get; set; }
    }
}
