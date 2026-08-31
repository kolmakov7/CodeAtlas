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
/// Regression test (eShop MCP verification, point 9): get_code must return the requested
/// source range, not a hard 20-line preview. Before the fix SearchService.GetCodeAsync
/// clamped every slice to PreviewMaxLines (20), so the MCP maxLines parameter (default 200,
/// max 1000) was a no-op and a coding agent could not read symbols longer than 20 lines.
/// </summary>
public sealed class GetCodeMaxLinesTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Guid _repo;
    private readonly Guid _file;
    private const string FilePath = "src/Big.cs";
    private const int TotalLines = 50;

    public GetCodeMaxLinesTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-getcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);
        _repo = Guid.NewGuid();
        _file = Guid.NewGuid();

        Directory.CreateDirectory(Path.Combine(_dataDir, "src"));
        File.WriteAllText(Path.Combine(_dataDir, "src", "Big.cs"),
            string.Join('\n', Enumerable.Range(1, TotalLines).Select(i => $"// line {i}")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<SearchService> InitializeAsync()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s)",
            new { i = _repo.ToString(), n = "unit", g = "file:///x", p = _dataDir, s = "ready" });
        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = _file.ToString(), r = _repo.ToString(), p = FilePath, l = "csharp" });

        var vectorFactory = Substitute.For<IVectorStoreFactory>();
        var embedFactory = Substitute.For<IEmbeddingProviderFactory>();
        var monitor = Substitute.For<IOptionsMonitor<SearchOptions>>();
        monitor.CurrentValue.Returns(new SearchOptions());
        return new SearchService(
            _factory,
            new FtsHelper(_factory),
            vectorFactory,
            embedFactory,
            NullLogger<SearchService>.Instance,
            monitor);
    }

    private static int LineCount(SourceSlice slice) => slice.Content.Split('\n').Length;

    [Fact]
    public async Task GetCode_default_should_keep_conservative_preview_clamp()
    {
        var service = await InitializeAsync();

        var slice = await service.GetCodeAsync(new GetCodeRequest(_repo, FilePath));

        LineCount(slice).Should().Be(20);
        slice.EndLine.Should().Be(20);
        slice.TotalLines.Should().Be(TotalLines);
    }

    [Fact]
    public async Task GetCode_explicit_max_lines_should_return_full_range()
    {
        var service = await InitializeAsync();

        var slice = await service.GetCodeAsync(new GetCodeRequest(_repo, FilePath, 1, TotalLines, MaxLines: 100));

        LineCount(slice).Should().Be(TotalLines);
        slice.StartLine.Should().Be(1);
        slice.EndLine.Should().Be(TotalLines);
        slice.Content.Should().Contain("// line 50");
    }

    [Fact]
    public async Task GetCode_max_lines_smaller_than_range_should_clamp_content_and_keep_short_preview()
    {
        var service = await InitializeAsync();

        var slice = await service.GetCodeAsync(new GetCodeRequest(_repo, FilePath, 1, TotalLines, MaxLines: 30));

        LineCount(slice).Should().Be(30);
        slice.EndLine.Should().Be(30);
        // Preview stays bounded by PreviewMaxLines (20) so callers can show a short summary.
        slice.Preview.Split('\n').Length.Should().Be(20);
    }

    [Fact]
    public async Task GetCode_max_lines_is_clamped_to_at_most_1000()
    {
        var service = await InitializeAsync();

        var slice = await service.GetCodeAsync(new GetCodeRequest(_repo, FilePath, 1, TotalLines, MaxLines: 10_000));

        LineCount(slice).Should().Be(TotalLines);
    }

    [Fact]
    public async Task GetCode_range_within_file_should_return_exact_lines()
    {
        var service = await InitializeAsync();

        var slice = await service.GetCodeAsync(new GetCodeRequest(_repo, FilePath, 10, 25, MaxLines: 200));

        LineCount(slice).Should().Be(16);
        slice.StartLine.Should().Be(10);
        slice.EndLine.Should().Be(25);
        slice.Content.Should().StartWith("// line 10").And.EndWith("// line 25");
    }
}
