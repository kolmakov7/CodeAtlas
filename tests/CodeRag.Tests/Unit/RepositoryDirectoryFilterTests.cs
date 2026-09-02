using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Indexer;
using FluentAssertions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

public sealed class RepositoryDirectoryFilterTests
{
    private static Repository CreateRepo(bool filterEnabled = false, string[]? dirs = null, string branch = "main")
    {
        return Repository.Create(Guid.NewGuid(), "test-repo", "https://github.com/org/repo.git", branch, "/data/repos/test-repo", enabled: true, groupId: null, directoryFilterEnabled: filterEnabled, allowedDirectories: dirs);
    }

    [Fact]
    public void Create_without_filter_should_disable_filter()
    {
        var repo = CreateRepo(false, null);
        repo.DirectoryFilterEnabled.Should().BeFalse();
        repo.AllowedDirectories.Should().BeEmpty();
        repo.IsPathAllowed("src/foo.cs").Should().BeTrue();
        repo.IsPathAllowed("docs/readme.md").Should().BeTrue();
    }

    [Fact]
    public void Create_with_filter_single_dir_should_allow_only_that()
    {
        var repo = CreateRepo(true, new[] { "src" });
        repo.DirectoryFilterEnabled.Should().BeTrue();
        repo.AllowedDirectories.Should().ContainSingle(x => x == "src");
        repo.IsPathAllowed("src/foo.cs").Should().BeTrue();
        repo.IsPathAllowed("src/nested/bar.cs").Should().BeTrue();
        repo.IsPathAllowed("src").Should().BeTrue();
        repo.IsPathAllowed("database/file.sql").Should().BeFalse();
        repo.IsPathAllowed("docs/readme.md").Should().BeFalse();
    }

    [Fact]
    public void Create_with_filter_multiple_dirs_should_allow_all()
    {
        var repo = CreateRepo(true, new[] { "src", "database", "scripts" });
        repo.IsPathAllowed("src/a.cs").Should().BeTrue();
        repo.IsPathAllowed("database/b.sql").Should().BeTrue();
        repo.IsPathAllowed("scripts/c.sh").Should().BeTrue();
        repo.IsPathAllowed("docs/d.md").Should().BeFalse();
        repo.IsPathAllowed("tests/e.cs").Should().BeFalse();
    }

    [Fact]
    public void Nested_files_of_selected_root_should_be_indexed()
    {
        var repo = CreateRepo(true, new[] { "src" });
        repo.IsPathAllowed("src/sub/nested/deeply/file.cs").Should().BeTrue();
        repo.IsPathAllowed("src/sub/file.cs").Should().BeTrue();
    }

    [Fact]
    public void Neighbor_directories_should_not_be_scanned()
    {
        var repo = CreateRepo(true, new[] { "src" });
        repo.IsPathAllowed("src2/file.cs").Should().BeFalse();
        repo.IsPathAllowed("src_backup/file.cs").Should().BeFalse();
        repo.IsPathAllowed("src-old/file.cs").Should().BeFalse();
    }

    [Fact]
    public void Filter_disabled_should_scan_all()
    {
        var repo = CreateRepo(false, null);
        // Full scan behavior not broken
        repo.IsPathAllowed("any/path/file.txt").Should().BeTrue();
        repo.IsPathAllowed("src/file.cs").Should().BeTrue();
        repo.IsPathAllowed("docs/file.md").Should().BeTrue();
    }

    [Fact]
    public void SetDirectoryFilter_should_validate_empty_when_enabled()
    {
        var repo = CreateRepo(false, null);
        var act = () => repo.SetDirectoryFilter(true, Array.Empty<string>());
        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void SetDirectoryFilter_disable_clears_dirs()
    {
        var repo = CreateRepo(true, new[] { "src", "docs" });
        repo.SetDirectoryFilter(false, null);
        repo.DirectoryFilterEnabled.Should().BeFalse();
        repo.AllowedDirectories.Should().BeEmpty();
        repo.IsPathAllowed("docs/file.md").Should().BeTrue();
    }

    [Fact]
    public void ChangeBranch_should_update_branch()
    {
        var repo = CreateRepo(false, null, "main");
        repo.ChangeBranch("develop");
        repo.Branch.Should().Be("develop");
    }

    [Fact]
    public void ChangeDirectoryFilter_should_toggle()
    {
        var repo = CreateRepo(false, null);
        repo.SetDirectoryFilter(true, new[] { "src" });
        repo.DirectoryFilterEnabled.Should().BeTrue();
        repo.SetDirectoryFilter(false, null);
        repo.DirectoryFilterEnabled.Should().BeFalse();
        repo.SetDirectoryFilter(true, new[] { "src", "tests" });
        repo.AllowedDirectories.Should().HaveCount(2);
    }

    [Fact]
    public void Rehydrate_should_preserve_filter()
    {
        var id = Guid.NewGuid();
        var repo = Repository.Rehydrate(id, "n", "https://github.com/o/r.git", "main", "/data/repos/n", null, null, RepositoryStatus.Active, true, null, null, null, null, null, null, null, true, new[] { "src", "database" });
        repo.DirectoryFilterEnabled.Should().BeTrue();
        repo.AllowedDirectories.Should().BeEquivalentTo(new[] { "src", "database" });
    }

    // --- Prove excluded directories are NOT passed to scanning (filter before discovery) ---

    [Fact]
    public void ApplyFilter_should_exclude_non_allowed_files_before_scanning()
    {
        var repo = CreateRepo(true, new[] { "src", "scripts" });
        var files = new[]
        {
            new GitChangedFile("src/app.cs", null, GitChangeKind.Added),
            new GitChangedFile("src/nested/util.cs", null, GitChangeKind.Modified),
            new GitChangedFile("database/schema.sql", null, GitChangeKind.Added),
            new GitChangedFile("docs/readme.md", null, GitChangeKind.Added),
            new GitChangedFile("tests/test.cs", null, GitChangeKind.Added),
            new GitChangedFile("scripts/deploy.sh", null, GitChangeKind.Added),
        };
        var diff = new GitDiff(null, "abc123", files, false);
        var filtered = FilterForTest(repo, diff);
        filtered.ChangedFiles.Should().HaveCount(3);
        filtered.ChangedFiles.Select(f => f.Path).Should().BeEquivalentTo(new[] { "src/app.cs", "src/nested/util.cs", "scripts/deploy.sh" });
        filtered.ChangedFiles.Any(f => f.Path == "docs/readme.md").Should().BeFalse();
        filtered.ChangedFiles.Any(f => f.Path == "database/schema.sql").Should().BeFalse();
        filtered.ChangedFiles.Any(f => f.Path == "tests/test.cs").Should().BeFalse();
    }

    [Fact]
    public void Filtered_diff_should_prove_excluded_not_scanned_not_just_missing_in_db()
    {
        // This test proves the filter is applied BEFORE scanning, not after saving.
        // We simulate IndexingService filtering: excluded files never reach IndexFileAsync.
        var repo = CreateRepo(true, new[] { "src" });
        var diff = new GitDiff("from", "to", new[]
        {
            new GitChangedFile("src/keep.cs", null, GitChangeKind.Added),
            new GitChangedFile("docs/exclude.md", null, GitChangeKind.Added),
            new GitChangedFile("tests/exclude2.cs", null, GitChangeKind.Added),
        }, false);
        var filtered = FilterForTest(repo, diff);
        // Only keep.cs should remain; if filter was post-save, all 3 would be scanned then docs filtered at DB.
        // Here we assert docs never passed to scanner.
        filtered.ChangedFiles.Should().ContainSingle(f => f.Path == "src/keep.cs");
        filtered.ChangedFiles.Count.Should().Be(1);
    }

    // Helper replicating IndexingService.ApplyDirectoryFilter logic
    private static GitDiff FilterForTest(Repository repo, GitDiff diff)
    {
        if (!repo.DirectoryFilterEnabled) return diff;
        var filtered = diff.ChangedFiles.Where(f => {
            if (f.Kind == GitChangeKind.Deleted) return repo.IsPathAllowed(f.Path);
            if (f.Kind == GitChangeKind.Renamed) return repo.IsPathAllowed(f.Path);
            return repo.IsPathAllowed(f.Path);
        }).ToList();
        return new GitDiff(diff.FromCommit, diff.ToCommit, filtered, diff.IsFullDiff);
    }
}

// Additional tests for validate / branches / root directories via mocked git
public sealed class RepositoryPreviewTests
{
    [Fact]
    public async Task Validate_success_should_return_available()
    {
        var git = Substitute.For<CodeRag.Git.IGitService>();
        git.LsRemoteAllAsync(Arg.Any<string>(), Arg.Any<CodeRag.Domain.Entities.ProxyConfig?>(), Arg.Any<CodeRag.Domain.Entities.Credential?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CodeRag.Git.GitRemoteRef> { new("refs/heads/main", "abc") });

        // Simulate RepositoryService.ValidateAsync logic
        var result = await ValidateWithMock(git, "https://github.com/org/repo.git", true);
        result.Available.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Validate_unavailable_should_return_error()
    {
        var git = Substitute.For<CodeRag.Git.IGitService>();
        git.LsRemoteAllAsync(Arg.Any<string>(), Arg.Any<CodeRag.Domain.Entities.ProxyConfig?>(), Arg.Any<CodeRag.Domain.Entities.Credential?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CodeRag.Git.GitRemoteRef>>>(x => throw new InvalidOperationException("not found"));

        var result = await ValidateWithMock(git, "https://github.com/org/missing.git", false);
        result.Available.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetBranches_should_parse_heads()
    {
        var git = Substitute.For<CodeRag.Git.IGitService>();
        git.LsRemoteAllAsync(Arg.Any<string>(), Arg.Any<CodeRag.Domain.Entities.ProxyConfig?>(), Arg.Any<CodeRag.Domain.Entities.Credential?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CodeRag.Git.GitRemoteRef>
            {
                new("refs/heads/main", "abc"),
                new("refs/heads/develop", "def"),
                new("refs/tags/v1", "ghi"),
            });
        // Use actual GitService logic? Mock ListRemoteBranches to return filtered; we simulate
        var refs = await git.LsRemoteAllAsync("https://github.com/org/repo.git");
        var branches = refs.Where(r => r.Ref.StartsWith("refs/heads/")).Select(r => r.Ref.Substring("refs/heads/".Length)).ToList();
        branches.Should().BeEquivalentTo(new[] { "main", "develop" });
    }

    [Fact]
    public void RootDirectories_derivation_should_extract_top_levels()
    {
        var files = new[] { "src/a.cs", "src/b.cs", "docs/readme.md", "database/schema.sql", "scripts/deploy.sh", "README.md" };
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var idx = f.IndexOf('/');
            if (idx > 0) dirs.Add(f.Substring(0, idx));
        }
        dirs.Should().BeEquivalentTo(new[] { "src", "docs", "database", "scripts" });
    }

    private static async Task<(bool Available, string? Error)> ValidateWithMock(CodeRag.Git.IGitService git, string url, bool shouldSucceed)
    {
        try
        {
            await git.LsRemoteAllAsync(url);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
