using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Persistence.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeRag.Tests.Unit;

/// <summary>Persistence tests for per-object usage statistics (symbol_usage).</summary>
public sealed class UsageStatsTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly DapperUsageRecorder _recorder;
    private readonly DapperUsageQueryService _query;

    public UsageStatsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);
        _recorder = new DapperUsageRecorder(_factory);
        _query = new DapperUsageQueryService(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<Guid> SeedSymbolAsync(Guid repo, string name)
    {
        var fileId = Guid.NewGuid();
        var symbolId = Guid.NewGuid();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync("INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s) ON CONFLICT(id) DO NOTHING",
            new { i = repo.ToString(), n = "t" + repo, g = "file:///x", p = "/x", s = "ready" });
        await conn.ExecuteAsync("INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = fileId.ToString(), r = repo.ToString(), p = $"src/{name}.sql", l = "sql" });
        await conn.ExecuteAsync(@"INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn,namespace,start_line,end_line)
VALUES(@i,@r,@f,'sql','Procedure',@n,@fqn,'dbo',1,10)",
            new { i = symbolId.ToString(), r = repo.ToString(), f = fileId.ToString(), n = name, fqn = "dbo." + name });
        return symbolId;
    }

    [Fact]
    public async Task Record_increments_and_aggregates_by_symbol()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        var repo = Guid.NewGuid();
        var a = await SeedSymbolAsync(repo, "ProcA");
        var b = await SeedSymbolAsync(repo, "ProcB");

        await _recorder.RecordSymbolsAsync("search_hit", "codeatlas", new[] { a, a, b });
        await _recorder.RecordSymbolsAsync("search_hit", "codeatlas", new[] { a });

        var scores = await _query.GetScoreByRepositoryAsync(repo, days: 30);
        scores[a].Should().Be(2);
        scores[b].Should().Be(1);
    }

    [Fact]
    public async Task GetTop_orders_by_count_and_scopes_to_repository()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        var repo1 = Guid.NewGuid();
        var repo2 = Guid.NewGuid();
        var hot = await SeedSymbolAsync(repo1, "Hot");
        var warm = await SeedSymbolAsync(repo1, "Warm");
        var other = await SeedSymbolAsync(repo2, "Other");

        await _recorder.RecordSymbolsAsync("lookup", "codeatlas", new[] { hot });
        await _recorder.RecordSymbolsAsync("lookup", "codeatlas", new[] { hot });
        await _recorder.RecordSymbolsAsync("lookup", "codeatlas", new[] { hot, warm });
        for (var i = 0; i < 5; i++)
            await _recorder.RecordSymbolsAsync("lookup", "codeatlas", new[] { other });

        var top1 = await _query.GetTopAsync(repo1, limit: 10, days: 30);
        top1.Select(x => x.Name).Should().Equal("Hot", "Warm");
        top1[0].UsageCount.Should().Be(3);

        var global = await _query.GetTopAsync(null, limit: 10, days: 30);
        global[0].Name.Should().Be("Other");
        global[0].UsageCount.Should().Be(5);
    }

    [Fact]
    public async Task Score_by_repository_ignores_other_repositories()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        var repo1 = Guid.NewGuid();
        var repo2 = Guid.NewGuid();
        var a = await SeedSymbolAsync(repo1, "A");
        var b = await SeedSymbolAsync(repo2, "B");

        await _recorder.RecordSymbolsAsync("lookup", "codeatlas", new[] { a, b, b });

        var scores = await _query.GetScoreByRepositoryAsync(repo1, days: 30);
        scores.Should().ContainKey(a);
        scores.Should().NotContainKey(b);
    }
}
