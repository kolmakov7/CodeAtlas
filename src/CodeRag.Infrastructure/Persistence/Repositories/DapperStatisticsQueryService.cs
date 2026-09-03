using System.Data;
using System.Text.Json;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads precomputed statistics snapshots (repository_stats / global_stats).
/// Falls back to the on-the-fly aggregation when a snapshot is missing.
/// Live aggregation spans main + chunks databases (chunks counts come from the chunks DB,
/// vector counts use vector_entries.repository_id — no cross-DB joins).
/// </summary>
public sealed class DapperStatisticsQueryService : IStatisticsQueryService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DapperStatisticsQueryService> _logger;

    private static readonly string[] DomainTokens = { "code", "text" };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public DapperStatisticsQueryService(IDbConnectionFactory factory, ILogger<DapperStatisticsQueryService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<RepositoryStatistics> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var snap = await conn.QuerySingleOrDefaultAsync<RepoStatsRow>(
            """
            SELECT rs.repository_id AS repository_id, r.name AS name,
                   rs.file_count AS file_count, rs.symbol_count AS symbol_count,
                   rs.chunk_count AS chunk_count, rs.vector_count AS vector_count,
                   rs.relation_count AS relation_count, rs.relations_by_type AS relations_by_type,
                   rs.language_breakdown AS language_breakdown,
                   rs.embedding_code_indexed AS embedding_code_indexed,
                   rs.embedding_code_pending AS embedding_code_pending,
                   rs.embedding_text_indexed AS embedding_text_indexed,
                   rs.embedding_text_pending AS embedding_text_pending,
                   rs.embeddings_failed AS embeddings_failed,
                   r.indexed_commit AS indexed_commit, r.current_commit AS current_commit, r.status AS status
            FROM repository_stats rs
            INNER JOIN repositories r ON r.id = rs.repository_id
            WHERE rs.repository_id = @id;
            """,
            new { id = repositoryId.ToString() }).ConfigureAwait(false);

        if (snap is not null)
        {
            return MapRepoSnapshot(repositoryId, snap);
        }

        _logger.LogDebug("No statistics snapshot for {Repo} — falling back to live aggregation", repositoryId);
        return await GetRepositoryStatisticsLiveAsync(repositoryId, conn, ct).ConfigureAwait(false);
    }

    public async Task<GlobalStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var global = await conn.QuerySingleOrDefaultAsync<GlobalStatsRow>("SELECT * FROM global_stats WHERE id = 1;").ConfigureAwait(false);

        if (global is null)
        {
            _logger.LogDebug("No global statistics snapshot — falling back to live aggregation");
            return await GetGlobalStatisticsLiveAsync(conn, ct).ConfigureAwait(false);
        }

        var repoRows = (await conn.QueryAsync<RepoStatsRow>(
            """
            SELECT rs.repository_id AS repository_id, r.name AS name,
                   rs.file_count AS file_count, rs.symbol_count AS symbol_count,
                   rs.chunk_count AS chunk_count, rs.vector_count AS vector_count,
                   rs.relation_count AS relation_count, rs.relations_by_type AS relations_by_type,
                   rs.language_breakdown AS language_breakdown,
                   rs.embedding_code_indexed AS embedding_code_indexed,
                   rs.embedding_code_pending AS embedding_code_pending,
                   rs.embedding_text_indexed AS embedding_text_indexed,
                   rs.embedding_text_pending AS embedding_text_pending,
                   rs.embeddings_failed AS embeddings_failed,
                   r.indexed_commit AS indexed_commit, r.current_commit AS current_commit, r.status AS status
            FROM repositories r
            LEFT JOIN repository_stats rs ON rs.repository_id = r.id
            ORDER BY r.name;
            """).ConfigureAwait(false)).ToArray();

        var repoStats = new List<RepositoryStatistics>(repoRows.Length);
        foreach (var r in repoRows)
        {
            if (r.repository_id is null) continue; // repo without snapshot: empty stats
            repoStats.Add(MapRepoSnapshot(Guid.Parse(r.repository_id), r));
        }

        return new GlobalStatistics(
            (int)global.repository_count, (int)global.file_count, (int)global.symbol_count,
            (int)global.chunk_count, (int)global.relation_count, ParseRelations(global.relations_by_type),
            (int)global.job_count, MapEmbeddings(global), (int)global.embeddings_failed, repoStats, ParseLanguages(global.language_breakdown));
    }

    public async Task<StorageSizes> GetStorageSizesAsync(CancellationToken ct = default)
    {
        long mainBytes = 0, vectorBytes = 0, reposBytes = 0;

        if (SqlDialect.IsPostgres(_factory))
        {
            // No local files — ask PostgreSQL for real database sizes.
            try
            {
                using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
                mainBytes = await PgDatabaseSizeAsync(conn, _factory.DbPath, ct).ConfigureAwait(false)
                           + await PgDatabaseSizeAsync(conn, _factory.ChunksDbPath, ct).ConfigureAwait(false);
                vectorBytes = await PgDatabaseSizeAsync(conn, _factory.VecDbPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "pg_database_size query failed");
            }
        }
        else
        {
            try { var fi = new FileInfo(_factory.DbPath); if (fi.Exists) mainBytes += fi.Length; } catch { }
            try { var fi = new FileInfo(_factory.ChunksDbPath); if (fi.Exists) mainBytes += fi.Length; } catch { }
            // Vector tables live in the separate vec database — report its real file size.
            try { var fi = new FileInfo(_factory.VecDbPath); if (fi.Exists) vectorBytes = fi.Length; } catch { }

            // Fallback: vec extension absent and vectors are held in memory — estimate from vector
            // count so Dashboard doesn't stay at 0.
            if (vectorBytes == 0 && mainBytes > 0)
            {
                try
                {
                    using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
                    var cnt = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries;").ConfigureAwait(false);
                    if (cnt > 0)
                    {
                        vectorBytes = cnt * 1024 * 4; // ~4KB per vector
                        if (vectorBytes > mainBytes) vectorBytes = mainBytes / 2;
                    }
                }
                catch { }
            }
        }

        try
        {
            var dataDir = _factory.DataDir;
            if (!string.IsNullOrWhiteSpace(dataDir))
            {
                var reposRoot = Path.Combine(dataDir, "repos");
                if (Directory.Exists(reposRoot))
                    reposBytes = Directory.GetFiles(reposRoot, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
            }
        }
        catch { }
        return new StorageSizes(mainBytes, vectorBytes, reposBytes);
    }

    private static async Task<long> PgDatabaseSizeAsync(IDbConnection conn, string databaseName, CancellationToken ct)
    {
        try
        {
            return await conn.ExecuteScalarAsync<long>("SELECT pg_database_size(@name);", new { name = databaseName }).ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    private static RepositoryStatistics MapRepoSnapshot(Guid repositoryId, RepoStatsRow snap)
    {
        var embeddingStats = new[]
        {
            new EmbeddingDomainStats("code", (int)snap.embedding_code_indexed, (int)snap.embedding_code_pending),
            new EmbeddingDomainStats("text", (int)snap.embedding_text_indexed, (int)snap.embedding_text_pending)
        };
        return new RepositoryStatistics(
            repositoryId, snap.name ?? "", (int)snap.file_count, (int)snap.symbol_count,
            (int)snap.chunk_count, (int)snap.vector_count, (int)snap.relation_count,
            ParseRelations(snap.relations_by_type), embeddingStats, (int)snap.embeddings_failed,
            snap.indexed_commit, snap.current_commit, snap.status ?? "", ParseLanguages(snap.language_breakdown));
    }

    private static IReadOnlyDictionary<string, int> ParseLanguages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, int>();
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOpts) ?? new Dictionary<string, int>(); }
        catch { return new Dictionary<string, int>(); }
    }

    private static IReadOnlyList<RelationTypeStats> ParseRelations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<RelationTypeStats>();
        try { return JsonSerializer.Deserialize<RelationTypeStats[]>(json, JsonOpts) ?? Array.Empty<RelationTypeStats>(); }
        catch { return Array.Empty<RelationTypeStats>(); }
    }

    private static IReadOnlyList<EmbeddingDomainStats> MapEmbeddings(GlobalStatsRow g)
    {
        return new[]
        {
            new EmbeddingDomainStats("code", (int)g.embedding_code_indexed, (int)g.embedding_code_pending),
            new EmbeddingDomainStats("text", (int)g.embedding_text_indexed, (int)g.embedding_text_pending)
        };
    }

    // --- On-the-fly aggregation (fallback only) ---

    private async Task<RepositoryStatistics> GetRepositoryStatisticsLiveAsync(Guid repositoryId, IDbConnection conn, CancellationToken ct)
    {
        var repoRow = await conn.QuerySingleOrDefaultAsync<RepoRow>("SELECT * FROM repositories WHERE id=@id;", new { id = repositoryId.ToString() }).ConfigureAwait(false);
        if (repoRow is null) throw new KeyNotFoundException($"Repository {repositoryId} not found.");
        var rid = repositoryId.ToString();
        var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);

        using var chunksConn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var chunkCount = await chunksConn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);

        var embeddingStats = new List<EmbeddingDomainStats>();
        foreach (var domain in DomainTokens)
            embeddingStats.Add(await GetDomainStatsAsync(conn, chunksConn, rid, domain, ct).ConfigureAwait(false));
        var failed = await GetFailedJobsAsync(conn, rid, ct).ConfigureAwait(false);
        var (relationCount, relationsByType) = await GetRelationStatsAsync(conn, rid, ct).ConfigureAwait(false);

        return new RepositoryStatistics(repositoryId, repoRow.name, (int)fileCount, (int)symbolCount, (int)chunkCount, (int)vectorCount,
            relationCount, relationsByType, embeddingStats, failed, repoRow.indexed_commit, repoRow.current_commit, repoRow.status);
    }

    private async Task<GlobalStatistics> GetGlobalStatisticsLiveAsync(IDbConnection conn, CancellationToken ct)
    {
        using var chunksConn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var repoRows = await conn.QueryAsync<RepoRow>("SELECT * FROM repositories ORDER BY name;").ConfigureAwait(false);
        var repoStats = new List<RepositoryStatistics>();
        foreach (var r in repoRows)
        {
            var rid = r.id;
            var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var chunkCount = await chunksConn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);

            var embeddingStats = new List<EmbeddingDomainStats>();
            foreach (var domain in DomainTokens)
                embeddingStats.Add(await GetDomainStatsAsync(conn, chunksConn, rid, domain, ct).ConfigureAwait(false));
            var failed = await GetFailedJobsAsync(conn, rid, ct).ConfigureAwait(false);
            var (relationCount, relationsByType) = await GetRelationStatsAsync(conn, rid, ct).ConfigureAwait(false);

            repoStats.Add(new RepositoryStatistics(Guid.Parse(r.id), r.name, (int)fileCount, (int)symbolCount, (int)chunkCount, (int)vectorCount, relationCount, relationsByType, embeddingStats, failed, r.indexed_commit, r.current_commit, r.status));
        }
        var totalFiles = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files;").ConfigureAwait(false);
        var totalSymbols = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols;").ConfigureAwait(false);
        var totalChunks = await chunksConn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks;").ConfigureAwait(false);
        var totalJobs = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM indexing_jobs;").ConfigureAwait(false);
        var (totalRelations, globalRelationsByType) = await GetRelationStatsAsync(conn, null, ct).ConfigureAwait(false);

        var globalEmbeddingStats = new List<EmbeddingDomainStats>();
        foreach (var domain in DomainTokens)
            globalEmbeddingStats.Add(await GetDomainStatsAsync(conn, chunksConn, null, domain, ct).ConfigureAwait(false));
        var globalFailed = await GetFailedJobsAsync(conn, null, ct).ConfigureAwait(false);

        return new GlobalStatistics(repoStats.Count, (int)totalFiles, (int)totalSymbols, (int)totalChunks, totalRelations, globalRelationsByType, (int)totalJobs, globalEmbeddingStats, globalFailed, repoStats);
    }

    /// <summary>Per-domain embedding progress (indexed = has vector, pending = no vector) for a repo (or null = global).
    /// total — chunks DB (embedding_domain), indexed — main DB (vector_entries.repository_id, no cross-DB join).</summary>
    private async Task<EmbeddingDomainStats> GetDomainStatsAsync(IDbConnection mainConn, IDbConnection chunksConn, string? rid, string domain, CancellationToken ct)
    {
        int total, indexed;
        try
        {
            total = rid is null
                ? await chunksConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chunks WHERE embedding_domain=@domain;", new { domain }).ConfigureAwait(false)
                : await chunksConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid AND embedding_domain=@domain;", new { rid, domain }).ConfigureAwait(false);
            indexed = rid is null
                ? await mainConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vector_entries WHERE domain=@domain;", new { domain }).ConfigureAwait(false)
                : await mainConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM vector_entries WHERE repository_id=@rid AND domain=@domain;", new { rid, domain }).ConfigureAwait(false);
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

    private sealed class RepoStatsRow
    {
        public string? repository_id { get; set; }
        public string? name { get; set; }
        public long file_count { get; set; }
        public long symbol_count { get; set; }
        public long chunk_count { get; set; }
        public long vector_count { get; set; }
        public long relation_count { get; set; }
        public string? relations_by_type { get; set; }
        public string? language_breakdown { get; set; }
        public long embedding_code_indexed { get; set; }
        public long embedding_code_pending { get; set; }
        public long embedding_text_indexed { get; set; }
        public long embedding_text_pending { get; set; }
        public long embeddings_failed { get; set; }
        public string? indexed_commit { get; set; }
        public string? current_commit { get; set; }
        public string? status { get; set; }
    }

    private sealed class GlobalStatsRow
    {
        public long repository_count { get; set; }
        public long file_count { get; set; }
        public long symbol_count { get; set; }
        public long chunk_count { get; set; }
        public long relation_count { get; set; }
        public string? relations_by_type { get; set; }
        public string? language_breakdown { get; set; }
        public long embedding_code_indexed { get; set; }
        public long embedding_code_pending { get; set; }
        public long embedding_text_indexed { get; set; }
        public long embedding_text_pending { get; set; }
        public long embeddings_failed { get; set; }
        public long job_count { get; set; }
    }
}
