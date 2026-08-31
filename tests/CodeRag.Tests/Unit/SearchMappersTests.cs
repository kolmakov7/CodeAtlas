using CodeRag.Api.Mappers;
using CodeRag.Search.Models;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class SearchMappersTests
{
    [Fact]
    public void ToDto_should_map_all_fields_and_fallback_repositoryName()
    {
        var item = new SearchResultItem(
            RepositoryId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            SymbolId: Guid.NewGuid(),
            Language: "csharp",
            FilePath: "src/Foo.cs",
            StartLine: 10,
            EndLine: 20,
            Score: 0.95,
            Preview: "line1\nline2",
            Origin: "fts")
        {
            ChunkId = Guid.NewGuid(),
            SymbolName = "Foo",
            Fqn = "Ns.Foo",
            RepositoryName = null
        };

        var dto = SearchResultMapper.ToDto(item);

        dto.RepositoryId.Should().Be(item.RepositoryId);
        dto.RepositoryName.Should().Be(item.RepositoryId.ToString()); // fallback
        dto.ChunkId.Should().Be(item.ChunkId);
        dto.FileId.Should().Be(item.FileId);
        dto.FilePath.Should().Be("src/Foo.cs");
        dto.SymbolId.Should().Be(item.SymbolId);
        dto.SymbolName.Should().Be("Foo");
        dto.Fqn.Should().Be("Ns.Foo");
        dto.Language.Should().Be("csharp");
        dto.Score.Should().Be(0.95);
        dto.Origin.Should().Be("fts");
        dto.Preview.Should().Be("line1\nline2");
    }

    [Fact]
    public void ToDto_should_keep_repositoryName_when_present()
    {
        var repoName = "my-repo";
        var item = new SearchResultItem(
            Guid.NewGuid(), Guid.NewGuid(), null, "python", "app.py", 1, 5, 1.0, "preview", "vector")
        { ChunkId = Guid.NewGuid(), RepositoryName = repoName };

        var dto = SearchResultMapper.ToDto(item);
        dto.RepositoryName.Should().Be(repoName);
    }

    [Fact]
    public void Truncate_should_limit_to_20_lines()
    {
        var longPreview = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line{i}"));
        var item = new SearchResultItem(
            Guid.NewGuid(), Guid.NewGuid(), null, "csharp", "Foo.cs", 1, 30, 0.5, longPreview, "fts")
        { ChunkId = Guid.NewGuid() };

        var dto = SearchResultMapper.ToDto(item);
        dto.Preview.Split('\n').Should().HaveCount(20);
        dto.Preview.Should().StartWith("line1");
        dto.Preview.Should().EndWith("line20");
    }

    [Fact]
    public void ToApiResponse_should_map_collection_and_elapsed()
    {
        var items = new[]
        {
            new SearchResultItem(Guid.NewGuid(), Guid.NewGuid(), null, "csharp", "a.cs", 1, 2, 0.1, "p1", "fts"){ ChunkId = Guid.NewGuid() },
            new SearchResultItem(Guid.NewGuid(), Guid.NewGuid(), null, "python", "b.py", 3, 4, 0.2, "p2", "vector"){ ChunkId = Guid.NewGuid() }
        };
        var result = new SearchResult(items, Total: 2, Warnings: new[] { "w1" }) { ElapsedMs = 42 };

        var response = SearchResultMapper.ToApiResponse(result);

        response.Items.Should().HaveCount(2);
        response.Total.Should().Be(2);
        response.Warnings.Should().ContainSingle().Which.Should().Be("w1");
        response.ElapsedMs.Should().Be(42);
    }

    [Theory]
    [InlineData(null, SearchMode.Hybrid)]
    [InlineData("", SearchMode.Hybrid)]
    [InlineData("hybrid", SearchMode.Hybrid)]
    [InlineData("Semantic", SearchMode.Semantic)]
    [InlineData("fts", SearchMode.FullText)] // alias -> FullText
    [InlineData("foo", SearchMode.Hybrid)] // unknown -> Hybrid fallback (default mode)
    [InlineData("FullText", SearchMode.FullText)]
    public void ParseMode_should_handle_cases(string? input, SearchMode expected)
    {
        SearchResultMapper.ParseMode(input).Should().Be(expected);
    }

    [Fact]
    public void Truncate_should_handle_empty_and_short()
    {
        SearchResultMapper.Truncate("", 20).Should().Be("");
        SearchResultMapper.Truncate("a\nb", 20).Should().Be("a\nb");
        SearchResultMapper.Truncate(null!, 20).Should().Be(null);
    }
}
