using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Search;
using CodeRag.Search.Models;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression tests for dual-domain ranking (eShop MCP iteration):
/// code and text live in separate embedding spaces (jina-code / jina-text), so
/// 1) the text domain uses a stricter absolute floor (MinTextSemanticScore) — the text model
///    scores irrelevant prose 0.33-0.46, which passed the generic 0.30 floor and leaked junk docs;
/// 2) Hybrid/Semantic normalize vector scores per domain and down-weight the text contribution
///    (TextDomainWeight), so code questions are not out-ranked by doc chunks;
/// 3) merged Semantic uses per-domain normalized scores so cross-model ranking is comparable.
/// </summary>
public sealed class SearchDomainRankingTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Guid _repo;
    private readonly Guid _codeFile;
    private readonly Guid _docFile;
    private readonly Guid _codeChunk;
    private readonly Guid _codeChunk2;
    private readonly Guid _docChunk;
    private readonly Guid _codeSymbol;

    public SearchDomainRankingTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);

        _repo = Guid.NewGuid();
        _codeFile = Guid.NewGuid();
        _docFile = Guid.NewGuid();
        _codeChunk = Guid.NewGuid();
        _codeChunk2 = Guid.NewGuid();
        _docChunk = Guid.NewGuid();
        _codeSymbol = Guid.NewGuid();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task InitializeDbAsync()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s)",
            new { i = _repo.ToString(), n = "unit", g = "file:///x", p = "/x", s = "ready" });
        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = _codeFile.ToString(), r = _repo.ToString(), p = "src/CatalogServices.cs", l = "csharp" });
        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = _docFile.ToString(), r = _repo.ToString(), p = "README.md", l = "markdown" });
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn) VALUES(@i,@r,@f,@l,@k,@n,@fqn)",
            new { i = _codeSymbol.ToString(), r = _repo.ToString(), f = _codeFile.ToString(), l = "csharp", k = "Class", n = "ZzzSymbol", fqn = "global::ZzzSymbol" });
        await conn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,symbol_id,language,start_line,end_line,content_hash,source) VALUES(@i,@r,@f,@s,@l,@sl,@el,@h,@src)",
            new { i = _codeChunk.ToString(), r = _repo.ToString(), f = _codeFile.ToString(), s = _codeSymbol.ToString(), l = "csharp", sl = 4, el = 16, h = "h1", src = "class ZzzSymbol { }" });
        await conn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,symbol_id,language,start_line,end_line,content_hash,source) VALUES(@i,@r,@f,@s,@l,@sl,@el,@h,@src)",
            new { i = _codeChunk2.ToString(), r = _repo.ToString(), f = _codeFile.ToString(), s = (object?)null, l = "csharp", sl = 17, el = 40, h = "h1b", src = "// more code" });
        await conn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,symbol_id,language,start_line,end_line,content_hash,source) VALUES(@i,@r,@f,@s,@l,@sl,@el,@h,@src)",
            new { i = _docChunk.ToString(), r = _repo.ToString(), f = _docFile.ToString(), s = (object?)null, l = "markdown", sl = 1, el = 40, h = "h2", src = "# Docs" });
    }

    private (SearchService Service, IVectorStore CodeStore, IVectorStore TextStore) BuildService(Action<SearchOptions>? configure = null)
    {
        var codeStore = Substitute.For<IVectorStore>();
        var textStore = Substitute.For<IVectorStore>();
        var vectorFactory = Substitute.For<IVectorStoreFactory>();
        vectorFactory.Code.Returns(codeStore);
        vectorFactory.Text.Returns(textStore);
        vectorFactory.Get(EmbeddingDomain.Code).Returns(codeStore);
        vectorFactory.Get(EmbeddingDomain.Text).Returns(textStore);

        var codeProvider = Substitute.For<IEmbeddingProvider>();
        var textProvider = Substitute.For<IEmbeddingProvider>();
        codeProvider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new[] { 1f, 0f, 0f } });
        textProvider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new[] { 0f, 1f, 0f } });
        var embedFactory = Substitute.For<IEmbeddingProviderFactory>();
        embedFactory.Code.Returns(codeProvider);
        embedFactory.Text.Returns(textProvider);
        embedFactory.Get(EmbeddingDomain.Code).Returns(codeProvider);
        embedFactory.Get(EmbeddingDomain.Text).Returns(textProvider);

        var options = new SearchOptions();
        configure?.Invoke(options);
        var monitor = Substitute.For<IOptionsMonitor<SearchOptions>>();
        monitor.CurrentValue.Returns(options);

        var service = new SearchService(
            _factory,
            new FtsHelper(_factory),
            vectorFactory,
            embedFactory,
            NullLogger<SearchService>.Instance,
            monitor);
        return (service, codeStore, textStore);
    }

    private static Task<IReadOnlyList<ScoredChunk>> ReturnsList(params ScoredChunk[] chunks)
        => Task.FromResult<IReadOnlyList<ScoredChunk>>(chunks);

    // Regression: jina-text scores irrelevant prose 0.33-0.46, which passed the generic 0.30 floor.
    // The text domain must use MinTextSemanticScore (0.55) so junk docs are filtered out.
    [Fact]
    public async Task SemanticText_irrelevant_prose_below_text_floor_is_filtered()
    {
        await InitializeDbAsync();
        var (service, _, textStore) = BuildService();
        textStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(new ScoredChunk(_docChunk, 0.40, 0.60, EmbeddingDomain.Text)));

        var result = await service.SearchCodeAsync(new SearchRequest(Query: "best pizza recipes in nyc", Mode: SearchMode.SemanticText));

        result.Items.Should().BeEmpty("text score 0.40 is below MinTextSemanticScore 0.55");
    }

    [Fact]
    public async Task SemanticText_relevant_doc_above_text_floor_is_kept()
    {
        await InitializeDbAsync();
        var (service, _, textStore) = BuildService();
        textStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(new ScoredChunk(_docChunk, 0.62, 0.38, EmbeddingDomain.Text)));

        var result = await service.SearchCodeAsync(new SearchRequest(Query: "What is the architecture of eShop?", Mode: SearchMode.SemanticText));

        result.Items.Should().ContainSingle().Which.ChunkId.Should().Be(_docChunk);
    }

    // Regression: raw cosines from two different models were min-max-normalized together, so the
    // domain whose model scored higher (typically text on prose) always dominated merged results.
    // Per-domain normalization + TextDomainWeight must let the code chunk win a code question even
    // when the text model's raw cosine is higher (0.90 vs 0.45).
    [Fact]
    public async Task Hybrid_code_chunk_beats_text_chunk_when_text_raw_cosine_is_higher()
    {
        await InitializeDbAsync();
        var (service, codeStore, textStore) = BuildService();
        codeStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(
                new ScoredChunk(_codeChunk, 0.45, 0.55, EmbeddingDomain.Code),
                new ScoredChunk(_codeChunk2, 0.35, 0.65, EmbeddingDomain.Code)));
        textStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(new ScoredChunk(_docChunk, 0.90, 0.10, EmbeddingDomain.Text)));

        var result = await service.SearchCodeAsync(new SearchRequest(Query: "retrieval plumbing for product listings", Mode: SearchMode.Hybrid));

        result.Items.Should().HaveCount(3);
        // code top: normVec (0.45-0.35)/(0.45-0.35)=1.0; text: 1.0*0.6=0.6; code low: 0.0
        result.Items[0].ChunkId.Should().Be(_codeChunk);
        result.Items[1].ChunkId.Should().Be(_docChunk);
        result.Items[2].ChunkId.Should().Be(_codeChunk2);
        result.Items[0].Score.Should().BeGreaterThan(result.Items[1].Score);
        result.Items[1].Score.Should().BeGreaterThan(result.Items[2].Score);
    }

    // Regression: merged Semantic must rank across models by per-domain normalized score,
    // not raw cosine (0.45 code vs 0.90 text raw would wrongly rank the doc first).
    [Fact]
    public async Task Semantic_merged_uses_per_domain_normalized_scores()
    {
        await InitializeDbAsync();
        var (service, codeStore, textStore) = BuildService();
        codeStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(new ScoredChunk(_codeChunk, 0.45, 0.55, EmbeddingDomain.Code)));
        textStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(new ScoredChunk(_docChunk, 0.90, 0.10, EmbeddingDomain.Text)));

        var result = await service.SearchCodeAsync(new SearchRequest(Query: "how does the basket service communicate", Mode: SearchMode.Semantic));

        result.Items.Should().HaveCount(2);
        result.Items[0].ChunkId.Should().Be(_codeChunk);
        result.Items[0].Score.Should().BeApproximately(1.0, 1e-9);
        result.Items[1].ChunkId.Should().Be(_docChunk);
        result.Items[1].Score.Should().BeApproximately(0.6, 1e-9);
    }

    // Single-domain modes keep raw cosine (0..1) — weak-query absolute filtering depends on it.
    [Fact]
    public async Task SemanticCode_keeps_raw_cosine_scores()
    {
        await InitializeDbAsync();
        var (service, codeStore, _) = BuildService();
        codeStore.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<VectorSearchFilter>(), Arg.Any<CancellationToken>())
            .Returns(ReturnsList(new ScoredChunk(_codeChunk, 0.45, 0.55, EmbeddingDomain.Code)));

        var result = await service.SearchCodeAsync(new SearchRequest(Query: "entity framework context", Mode: SearchMode.SemanticCode));

        result.Items.Should().ContainSingle().Which.Score.Should().BeApproximately(0.45, 1e-9);
    }
}
