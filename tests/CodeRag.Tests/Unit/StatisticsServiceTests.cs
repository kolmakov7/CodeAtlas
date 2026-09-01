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
}
