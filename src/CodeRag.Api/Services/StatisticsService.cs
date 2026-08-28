using CodeRag.Api.Models;
using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Api.Services;

public sealed class StatisticsService
{
    private readonly SqliteConnectionFactory _factory;
    public StatisticsService(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<RepositoryStatisticsDto> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var repoRow = await conn.QuerySingleOrDefaultAsync<RepoRow>("SELECT * FROM repositories WHERE id=@id;", new { id = repositoryId.ToString() }).ConfigureAwait(false);
        if (repoRow is null) throw new KeyNotFoundException($"Repository {repositoryId} not found.");

        var rid = repositoryId.ToString();
        var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        var chunkCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
        long vectorCount = 0;
        try { vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id=@rid);", new { rid }).ConfigureAwait(false); } catch { /* ignore */ }

        return new RepositoryStatisticsDto(
            repositoryId, repoRow.name, (int)fileCount, (int)symbolCount, (int)chunkCount, (int)vectorCount,
            repoRow.indexed_commit, repoRow.current_commit, repoRow.status);
    }

    public async Task<GlobalStatisticsDto> GetGlobalStatisticsAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var repoRows = await conn.QueryAsync<RepoRow>("SELECT * FROM repositories ORDER BY name;").ConfigureAwait(false);
        var repoStats = new List<RepositoryStatisticsDto>();

        foreach (var r in repoRows)
        {
            var rid = r.id;
            var fileCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var symbolCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            var chunkCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks WHERE repository_id=@rid;", new { rid }).ConfigureAwait(false);
            long vectorCount = 0;
            try { vectorCount = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM vector_entries WHERE chunk_id IN (SELECT id FROM chunks WHERE repository_id=@rid);", new { rid }).ConfigureAwait(false); } catch { }

            repoStats.Add(new RepositoryStatisticsDto(
                Guid.Parse(r.id), r.name, (int)fileCount, (int)symbolCount, (int)chunkCount, (int)vectorCount,
                r.indexed_commit, r.current_commit, r.status));
        }

        var totalFiles = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM files;").ConfigureAwait(false);
        var totalSymbols = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM symbols;").ConfigureAwait(false);
        var totalChunks = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM chunks;").ConfigureAwait(false);
        var totalJobs = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM indexing_jobs;").ConfigureAwait(false);

        return new GlobalStatisticsDto(
            repoStats.Count, (int)totalFiles, (int)totalSymbols, (int)totalChunks, (int)totalJobs, repoStats);
    }

    private sealed class RepoRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string status { get; set; } = "";
        public string? current_commit { get; set; }
        public string? indexed_commit { get; set; }
    }
}
