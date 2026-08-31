using CodeRag.Api.Mappers;
using CodeRag.Search.Models;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class GetCodeResponseMapperTests
{
    [Fact]
    public void ToDto_should_map_all_fields_and_truncate_preview()
    {
        var longPreview = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"l{i}"));
        var slice = new SourceSlice(
            RepositoryId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            Path: "src/Foo.cs",
            Language: "csharp",
            StartLine: 1,
            EndLine: 10,
            TotalLines: 100,
            Content: "full content",
            Preview: longPreview);

        var dto = GetCodeResponseMapper.ToDto(slice);

        dto.RepositoryId.Should().Be(slice.RepositoryId);
        dto.FileId.Should().Be(slice.FileId);
        dto.Path.Should().Be("src/Foo.cs");
        dto.Language.Should().Be("csharp");
        dto.StartLine.Should().Be(1);
        dto.EndLine.Should().Be(10);
        dto.TotalLines.Should().Be(100);
        dto.Content.Should().Be("full content");
        dto.Preview.Split('\n').Should().HaveCount(20);
    }

    [Fact]
    public void ToDto_should_not_truncate_short_preview()
    {
        var slice = new SourceSlice(Guid.NewGuid(), Guid.NewGuid(), "a.cs", "csharp", 1, 5, 10, "c", "short\npreview");
        var dto = GetCodeResponseMapper.ToDto(slice);
        dto.Preview.Should().Be("short\npreview");
        dto.Content.Should().Be("c");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("10", 10, 10)]
    [InlineData("10-20", 10, 20)]
    [InlineData("10:20", 10, 20)]
    [InlineData("10,20", 10, 20)]
    [InlineData(" 5 - 15 ", 5, 15)]
    [InlineData("invalid", null, null)]
    [InlineData("10-abc", 10, null)]
    public void ParseLines_should_parse_variants(string? input, int? expectedStart, int? expectedEnd)
    {
        var (s, e) = GetCodeResponseMapper.ParseLines(input);
        s.Should().Be(expectedStart);
        e.Should().Be(expectedEnd);
    }

    [Fact]
    public void ParseLines_should_trim_and_handle_single()
    {
        var (s, e) = GetCodeResponseMapper.ParseLines(" 42 ");
        s.Should().Be(42);
        e.Should().Be(42);
    }
}
