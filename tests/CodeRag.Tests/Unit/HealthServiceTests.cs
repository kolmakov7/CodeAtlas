using CodeRag.Api.Services;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Indexer;
using CodeRag.Infrastructure.Vectors;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

public sealed class HealthServiceTests
{
    private static HealthService CreateSut(
        IDbHealthProbe? db = null,
        IStorageProbe? storage = null,
        IVectorStore? vec = null,
        IEmbeddingProvider? emb = null,
        IJobRunner? runner = null)
    {
        if (db is null) { db = Substitute.For<IDbHealthProbe>(); db.CheckAsync(Arg.Any<CancellationToken>()).Returns(new HealthProbeResult("Healthy", null, 1)); }
        if (storage is null) { storage = Substitute.For<IStorageProbe>(); storage.Check().Returns(new HealthProbeResult("Healthy", null, null)); }
        if (vec is null)
        {
            vec = Substitute.For<IVectorStore>();
            vec.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter?>(), Arg.Any<CancellationToken>()).Returns(new List<ScoredChunk>());
            vec.GetIndexSummaryAsync(Arg.Any<CancellationToken>()).Returns(VectorIndexSummary.Empty);
        }
        if (emb is null)
        {
            emb = Substitute.For<IEmbeddingProvider>();
            emb.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new[] { new float[4] });
            emb.Dimensions.Returns(4);
            emb.Model.Returns("test-model");
            emb.ContextLength.Returns((int?)null);
        }

        var vecFactory = Substitute.For<IVectorStoreFactory>();
        vecFactory.Code.Returns(vec);
        vecFactory.Text.Returns(vec);
        vecFactory.Get(Arg.Any<CodeRag.Domain.Enums.EmbeddingDomain>()).Returns(vec);

        var embFactory = Substitute.For<IEmbeddingProviderFactory>();
        embFactory.Code.Returns(emb);
        embFactory.Text.Returns(emb);
        embFactory.Get(Arg.Any<CodeRag.Domain.Enums.EmbeddingDomain>()).Returns(emb);

        runner ??= Substitute.For<IJobRunner>();
        return new HealthService(db, storage, vecFactory, embFactory, runner, NullLogger<HealthService>.Instance);
    }

    [Fact]
    public async Task CheckDetailedAsync_should_be_Healthy_when_all_ok()
    {
        var sut = CreateSut();
        var details = await sut.CheckDetailedAsync();
        details.Status.Should().Be("Healthy");
        details.Database.Status.Should().Be("Healthy");
        details.Storage.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task CheckDetailedAsync_should_be_Degraded_when_db_unhealthy()
    {
        var db = Substitute.For<IDbHealthProbe>();
        db.CheckAsync(Arg.Any<CancellationToken>()).Returns(new HealthProbeResult("Unhealthy", "db down", 2));
        var sut = CreateSut(db: db);
        var details = await sut.CheckDetailedAsync();
        details.Status.Should().Be("Degraded");
    }

    [Fact]
    public async Task CheckReadyAsync_should_fail_on_db()
    {
        var db = Substitute.For<IDbHealthProbe>();
        db.CheckAsync(Arg.Any<CancellationToken>()).Returns(new HealthProbeResult("Unhealthy", "fail", 1));
        var sut = CreateSut(db: db);
        var (healthy, msg) = await sut.CheckReadyAsync();
        healthy.Should().BeFalse();
        msg.Should().Contain("database");
    }

    [Fact]
    public async Task CheckEmbedding_degraded_does_not_block_ready()
    {
        var emb = Substitute.For<IEmbeddingProvider>();
        emb.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<float[][]>(new Exception("embed down")));
        var sut = CreateSut(emb: emb);
        var details = await sut.CheckDetailedAsync();
        details.Embedding.Status.Should().Be("Degraded");
        details.Warnings.Should().Contain(w => w.Contains("embeddings"));
        // overall still Healthy because embedding not blocking
        details.Status.Should().Be("Healthy");
    }
}
