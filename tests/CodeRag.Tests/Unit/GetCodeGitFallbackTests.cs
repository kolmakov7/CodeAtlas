using CodeRag.Git;
using CodeRag.Git.Auth;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Stores;
using CodeRag.Search;
using CodeRag.Search.Models;
using CodeRag.Search.Services;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression test (bcs_eva_app MCP verification): get_code for no-checkout repos must return
/// the verbatim file content (git show of the indexed commit), not chunk-stitched text.
/// Analyzer chunks are context-decorated (namespace/class headers, [continued] markers,
/// overlapping slices), so stitching them produces duplicated, mis-numbered "mud".
/// </summary>
public sealed class GetCodeGitFallbackTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _reposRoot;
    private readonly string _repoDir;
    private readonly string? _originalDataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Guid _repo;
    private readonly Guid _file;
    private string _sha = string.Empty;
    private const string FilePath = "src/Big.cs";
    private const int TotalLines = 50;

    public GetCodeGitFallbackTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-getcode-git-" + Guid.NewGuid().ToString("N"));
        _reposRoot = Path.Combine(_dataDir, "repos");
        _repoDir = Path.Combine(_reposRoot, "test-repo");
        Directory.CreateDirectory(Path.Combine(_repoDir, "src"));
        _originalDataDir = Environment.GetEnvironmentVariable("CODERAG__DataDir");
        Environment.SetEnvironmentVariable("CODERAG__DataDir", _dataDir);

        File.WriteAllText(Path.Combine(_repoDir, "src", "Big.cs"),
            string.Join('\n', Enumerable.Range(1, TotalLines).Select(i => $"// line {i}")));
        Git("init .");
        Git("add -A");
        Git("-c user.name=test -c user.email=test@test commit -m init");
        _sha = Git("rev-parse HEAD").Trim();

        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);
        _repo = Guid.NewGuid();
        _file = Guid.NewGuid();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODERAG__DataDir", _originalDataDir);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private string Git(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"-C \"{_repoDir}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    private async Task<FileContentService> InitializeAsync(IGitService? git)
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,branch,local_path,status,indexed_commit) VALUES(@i,@n,@g,@b,@p,@s,@c)",
            new { i = _repo.ToString(), n = "gitfallback", g = "file:///x", b = "main", p = _repoDir, s = "ready", c = _sha });
        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = _file.ToString(), r = _repo.ToString(), p = FilePath, l = "csharp" });

        var monitor = Substitute.For<IOptionsMonitor<SearchOptions>>();
        monitor.CurrentValue.Returns(new SearchOptions());
        return new FileContentService(new RepositoryStore(_factory), new FileStore(_factory), new ChunkStore(_factory), monitor, git);
    }

    private static GitService CreateGitService(string reposRoot)
        => new(Options.Create(new GitOptions { ReposRoot = reposRoot }), new NoopCredentialProtector(), NullLogger<GitService>.Instance);

    [Fact]
    public async Task GetCode_no_checkout_should_return_verbatim_git_content()
    {
        var service = await InitializeAsync(CreateGitService(_reposRoot));
        File.Delete(Path.Combine(_repoDir, "src", "Big.cs")); // simulate --no-checkout repo

        var slice = await service.GetAsync(new GetCodeRequest(_repo, FilePath, 1, TotalLines, MaxLines: 100), CancellationToken.None);

        slice.StartLine.Should().Be(1);
        slice.EndLine.Should().Be(TotalLines);
        slice.TotalLines.Should().Be(TotalLines);
        slice.Content.Should().StartWith("// line 1").And.EndWith($"// line {TotalLines}");
        slice.Content.Should().NotContain("[continued]");
        slice.Content.Split('\n').Length.Should().Be(TotalLines);
    }

    [Fact]
    public async Task GetCode_no_checkout_range_should_return_exact_lines()
    {
        var service = await InitializeAsync(CreateGitService(_reposRoot));
        File.Delete(Path.Combine(_repoDir, "src", "Big.cs"));

        var slice = await service.GetAsync(new GetCodeRequest(_repo, FilePath, 10, 25, MaxLines: 200), CancellationToken.None);

        slice.StartLine.Should().Be(10);
        slice.EndLine.Should().Be(25);
        slice.Content.Should().StartWith("// line 10").And.EndWith("// line 25");
    }

    [Fact]
    public async Task GetCode_no_checkout_git_failure_should_fall_back_to_chunks()
    {
        var service = await InitializeAsync(CreateGitService(_reposRoot));
        File.Delete(Path.Combine(_repoDir, "src", "Big.cs"));
        Directory.Delete(Path.Combine(_repoDir, ".git"), recursive: true); // git show must fail

        using var conn = await _factory.CreateChunksAsync();
        await conn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,language,start_line,end_line,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@l,@s,@e,@h,@src,@d)",
            new { i = Guid.NewGuid().ToString(), r = _repo.ToString(), f = _file.ToString(), l = "csharp", s = 1, e = 2, h = "x", src = "chunk source A\nchunk source B", d = "code" });

        var slice = await service.GetAsync(new GetCodeRequest(_repo, FilePath, 1, 3, MaxLines: 100), CancellationToken.None);

        // AppendLine-per-chunk leaves a trailing blank line in the stitched text (pre-existing behavior).
        slice.Content.Should().StartWith("chunk source A\nchunk source B");
    }

    [Fact]
    public async Task GetCode_without_git_service_should_stitch_chunks()
    {
        var service = await InitializeAsync(git: null);
        File.Delete(Path.Combine(_repoDir, "src", "Big.cs"));

        using var conn = await _factory.CreateChunksAsync();
        await conn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,language,start_line,end_line,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@l,@s,@e,@h,@src,@d)",
            new { i = Guid.NewGuid().ToString(), r = _repo.ToString(), f = _file.ToString(), l = "csharp", s = 1, e = 2, h = "x", src = "stitched line 1\nstitched line 2", d = "code" });

        var slice = await service.GetAsync(new GetCodeRequest(_repo, FilePath, 1, 2, MaxLines: 100), CancellationToken.None);

        slice.Content.Should().Be("stitched line 1\nstitched line 2");
    }
}
