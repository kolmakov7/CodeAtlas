using CodeRag.Api.Services;
using CodeRag.Domain.Ports;
using FluentAssertions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

public sealed class StatisticsServiceTests
{
    [Fact]
    public async Task GetRepositoryStatistics_maps_correctly()
    {
        var query = Substitute.For<IStatisticsQueryService>();
        var repoId = Guid.NewGuid();
        query.GetRepositoryStatisticsAsync(repoId, Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatistics(repoId, "demo", 5, 10, 20, 15, 7, Array.Empty<RelationTypeStats>(), Array.Empty<EmbeddingDomainStats>(), 0, "aaa", "aaa", "Active"));

        var sut = new StatisticsService(query);
        var dto = await sut.GetRepositoryStatisticsAsync(repoId);
        dto.RepositoryId.Should().Be(repoId);
        dto.Name.Should().Be("demo");
        dto.FileCount.Should().Be(5);
        dto.ChunkCount.Should().Be(20);
    }

    [Fact]
    public async Task GetGlobalStatistics_maps_repositories()
    {
        var query = Substitute.For<IStatisticsQueryService>();
        query.GetGlobalStatisticsAsync(Arg.Any<CancellationToken>())
            .Returns(new GlobalStatistics(1, 10, 20, 30, 7, Array.Empty<RelationTypeStats>(), 2, Array.Empty<EmbeddingDomainStats>(), 0, new[] { new RepositoryStatistics(Guid.NewGuid(), "r1", 1, 2, 3, 4, 3, Array.Empty<RelationTypeStats>(), Array.Empty<EmbeddingDomainStats>(), 0, null, null, "Active") }));
        query.GetStorageSizesAsync(Arg.Any<CancellationToken>()).Returns(new StorageSizes(100, 200, 300));
        var sut = new StatisticsService(query);
        var global = await sut.GetGlobalStatisticsAsync();
        global.RepositoryCount.Should().Be(1);
        global.Repositories.Should().HaveCount(1);
        global.Storage.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRepositoryStatistics_maps_language_breakdown()
    {
        var query = Substitute.For<IStatisticsQueryService>();
        var repoId = Guid.NewGuid();
        query.GetRepositoryStatisticsAsync(repoId, Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatistics(repoId, "demo", 5, 10, 20, 15, 7,
                Array.Empty<RelationTypeStats>(), Array.Empty<EmbeddingDomainStats>(), 0,
                null, null, "Active", new Dictionary<string, int> { ["csharp"] = 4, ["python"] = 1 }));

        var sut = new StatisticsService(query);
        var dto = await sut.GetRepositoryStatisticsAsync(repoId);
        dto.LanguageBreakdown.Should().NotBeNull();
        dto.LanguageBreakdown!["csharp"].Should().Be(4);
        dto.LanguageBreakdown!["python"].Should().Be(1);
    }

    [Fact]
    public async Task GetMcpUsageStats_maps_aggregates()
    {
        var query = Substitute.For<IStatisticsQueryService>();
        var mcp = Substitute.For<IMcpUsageQueryService>();
        mcp.GetStatsAsync(30, Arg.Any<CancellationToken>()).Returns(new McpUsageStats(
            10, 2, 30,
            new[] { new McpToolUsageTotal("search_code", 8, 2), new McpToolUsageTotal("get_code", 2, 0) },
            new[] { new McpDailyUsage("2026-09-01", 5), new McpDailyUsage("2026-09-02", 5) }));

        var sut = new StatisticsService(query, mcp);
        var dto = await sut.GetMcpUsageStatsAsync(30);
        dto.TotalCalls.Should().Be(10);
        dto.TotalErrors.Should().Be(2);
        dto.Days.Should().Be(30);
        dto.ByTool.Should().Contain(t => t.Tool == "search_code" && t.Total == 8 && t.Errors == 2);
        dto.ByDay.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMcpUsageStats_clamps_days_and_is_empty_without_query_service()
    {
        var query = Substitute.For<IStatisticsQueryService>();
        var mcp = Substitute.For<IMcpUsageQueryService>();
        mcp.GetStatsAsync(1, Arg.Any<CancellationToken>())
            .Returns(new McpUsageStats(0, 0, 1, Array.Empty<McpToolUsageTotal>(), Array.Empty<McpDailyUsage>()));

        var sut = new StatisticsService(query, mcp);
        var clamped = await sut.GetMcpUsageStatsAsync(0);
        clamped.Days.Should().Be(1);

        var without = new StatisticsService(query);
        var empty = await without.GetMcpUsageStatsAsync(30);
        empty.TotalCalls.Should().Be(0);
        empty.ByTool.Should().BeEmpty();
        empty.ByDay.Should().BeEmpty();
    }
}
