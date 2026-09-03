using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Persistence.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Persistence tests for the precomputed statistics snapshots (repository_stats / global_stats),
/// language breakdown, and MCP tool-usage recording/aggregation (migration 008).
/// </summary>
public sealed class StatisticsAndMcpUsageTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;

    public StatisticsAndMcpUsageTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private DapperStatisticsRefresher Refresher => new(_factory, NullLogger<DapperStatisticsRefresher>.Instance);
    private DapperStatisticsQueryService Queries => new(_factory, NullLogger<DapperStatisticsQueryService>.Instance);
    private DapperMcpUsageRecorder Recorder => new(_factory, NullLogger<DapperMcpUsageRecorder>.Instance);
    private DapperMcpUsageQueryService McpQueries => new(_factory, NullLogger<DapperMcpUsageQueryService>.Instance);

    private async Task InitializeAsync()
    {
        await new DbInitializer(_factory, NullLogger<DbInitializer>.Instance).InitializeAsync();
    }

    /// <summary>Seeds a repository with files (per-language), a couple of symbols, chunks and vectors.</summary>
    private async Task<Guid> SeedRepoAsync(string name, params (string Path, string Language)[] files)
    {
        var repo = Guid.NewGuid();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s)",
            new { i = repo.ToString(), n = name, g = "file:///x", p = "/x", s = "ready" });
        foreach (var (path, language) in files)
        {
            var file = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
                new { i = file.ToString(), r = repo.ToString(), p = path, l = language });
        }

        var fileId = await conn.ExecuteScalarAsync<string>("SELECT id FROM files WHERE repository_id=@r LIMIT 1;", new { r = repo.ToString() });
        var symbolA = Guid.NewGuid();
        var symbolB = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name) VALUES(@i,@r,@f,@l,@k,@n)",
            new { i = symbolA.ToString(), r = repo.ToString(), f = fileId, l = "csharp", k = "Class", n = "A" });
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name) VALUES(@i,@r,@f,@l,@k,@n)",
            new { i = symbolB.ToString(), r = repo.ToString(), f = fileId, l = "csharp", k = "Method", n = "B" });

        var chunkA = Guid.NewGuid();
        var chunkB = Guid.NewGuid();
        var chunkText = Guid.NewGuid();
        using var chunksConn = await _factory.CreateChunksAsync();
        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@h,@s,@d)",
            new { i = chunkA.ToString(), r = repo.ToString(), f = fileId, h = "h1", s = "code", d = "code" });
        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@h,@s,@d)",
            new { i = chunkB.ToString(), r = repo.ToString(), f = fileId, h = "h2", s = "code", d = "code" });
        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@h,@s,@d)",
            new { i = chunkText.ToString(), r = repo.ToString(), f = fileId, h = "h3", s = "text", d = "text" });

        // One vector for chunkA (code domain), none for the others.
        await conn.ExecuteAsync(
            "INSERT INTO vector_entries(chunk_id,repository_id,vector_id,model,dim,domain) VALUES(@c,@r,@v,@m,@d,@dom)",
            new { c = chunkA.ToString(), r = repo.ToString(), v = Guid.NewGuid().ToString("N"), m = "test-embedding", d = 8, dom = "code" });

        await conn.ExecuteAsync(
            "INSERT INTO relations(from_symbol_id,to_symbol_id,relation_type,repository_id) VALUES(@f,@t,@ty,@r)",
            new { f = symbolA.ToString(), t = symbolB.ToString(), ty = "Calls", r = repo.ToString() });

        return repo;
    }

    [Fact]
    public async Task RefreshRepository_writes_snapshot_with_counts_and_language_breakdown()
    {
        await InitializeAsync();
        var repo = await SeedRepoAsync("demo",
            ("A.cs", "csharp"), ("B.cs", "csharp"), ("C.cs", "csharp"),
            ("d.py", "python"), ("e.md", "markdown"));

        await Refresher.RefreshRepositoryAsync(repo);

        var stats = await Queries.GetRepositoryStatisticsAsync(repo);
        stats.Name.Should().Be("demo");
        stats.FileCount.Should().Be(5);
        stats.SymbolCount.Should().Be(2);
        stats.ChunkCount.Should().Be(3);
        stats.VectorCount.Should().Be(1);
        stats.RelationCount.Should().Be(1);
        stats.RelationsByType.Should().ContainSingle().Which.RelationType.Should().Be("Calls");
        stats.LanguageBreakdown.Should().NotBeNull();
        stats.LanguageBreakdown!["csharp"].Should().Be(3);
        stats.LanguageBreakdown!["python"].Should().Be(1);
        stats.LanguageBreakdown!["markdown"].Should().Be(1);

        // code domain: 2 chunks, 1 embedded; text domain: 1 chunk, 0 embedded.
        stats.EmbeddingStats.Should().Contain(d => d.Domain == "code" && d.Indexed == 1 && d.Pending == 1);
        stats.EmbeddingStats.Should().Contain(d => d.Domain == "text" && d.Indexed == 0 && d.Pending == 1);
    }

    [Fact]
    public async Task RefreshGlobal_aggregates_repository_snapshots()
    {
        await InitializeAsync();
        await SeedRepoAsync("alpha", ("A.cs", "csharp"), ("B.cs", "csharp"));
        var beta = await SeedRepoAsync("beta", ("p.py", "python"));
        await Refresher.RefreshRepositoryAsync(beta);

        await Refresher.RefreshGlobalAsync();

        var global = await Queries.GetGlobalStatisticsAsync();
        global.RepositoryCount.Should().Be(2);
        // alpha has no snapshot: its counts are not in the snapshot totals.
        global.FileCount.Should().Be(1);
        global.ChunkCount.Should().Be(3);
        global.LanguageBreakdown.Should().NotBeNull();
        global.LanguageBreakdown!.Should().ContainKey("python");
        global.Repositories.Should().ContainSingle();
        global.Repositories[0].Name.Should().Be("beta");
    }

    [Fact]
    public async Task EnsureAsync_fills_snapshots_when_missing_and_is_idempotent()
    {
        await InitializeAsync();
        await SeedRepoAsync("demo", ("A.cs", "csharp"));

        await Refresher.EnsureAsync();
        using var conn = await _factory.CreateAsync();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM repository_stats;")).Should().Be(1);
        (await conn.ExecuteScalarAsync<long>("SELECT repository_count FROM global_stats WHERE id=1;")).Should().Be(1);

        // Second run must not duplicate or fail: global_stats stays a single row.
        await Refresher.EnsureAsync();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM global_stats;")).Should().Be(1);
    }

    [Fact]
    public async Task GetRepositoryStatistics_falls_back_to_live_aggregation_without_snapshot()
    {
        await InitializeAsync();
        var repo = await SeedRepoAsync("demo", ("A.cs", "csharp"), ("B.cs", "csharp"), ("C.cs", "csharp"));

        var stats = await Queries.GetRepositoryStatisticsAsync(repo);
        stats.FileCount.Should().Be(3);
        stats.ChunkCount.Should().Be(3);
        stats.LanguageBreakdown.Should().BeNull(); // live path mirrors the legacy response shape
    }

    [Fact]
    public async Task McpUsage_is_recorded_and_aggregated_by_tool_and_day()
    {
        await InitializeAsync();
        var repo = Guid.NewGuid();

        Recorder.Record("search_code", success: true, durationMs: 12, repositoryId: repo);
        Recorder.Record("search_code", success: true, durationMs: 8, repositoryId: repo);
        Recorder.Record("get_code", success: false, durationMs: 3, repositoryId: repo);
        Recorder.Record("embedding_status", success: true, durationMs: 1);

        // Record() is fire-and-forget: poll until all rows land.
        long count = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var conn = await _factory.CreateAsync();
            count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mcp_tool_usage;");
            if (count >= 4) break;
            await Task.Delay(50);
        }
        count.Should().Be(4, "all four invocations should be recorded");

        var stats = await McpQueries.GetStatsAsync(days: 7);
        stats.TotalCalls.Should().Be(4);
        stats.TotalErrors.Should().Be(1);
        stats.Days.Should().Be(7);
        stats.ByDay.Should().HaveCount(7);
        stats.ByDay.Sum(d => d.Total).Should().Be(4);
        stats.ByTool.Should().Contain(t => t.Tool == "search_code" && t.Total == 2 && t.Errors == 0);
        stats.ByTool.Should().Contain(t => t.Tool == "get_code" && t.Total == 1 && t.Errors == 1);
    }

    [Fact]
    public async Task McpUsage_days_is_clamped_to_supported_range()
    {
        await InitializeAsync();

        var min = await McpQueries.GetStatsAsync(days: 0);
        min.Days.Should().Be(1);

        var max = await McpQueries.GetStatsAsync(days: 10_000);
        max.Days.Should().Be(365);
        max.ByDay.Should().HaveCount(365);
    }
}
