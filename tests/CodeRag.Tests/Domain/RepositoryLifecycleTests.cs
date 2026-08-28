using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using FluentAssertions;
using NSubstitute;

namespace CodeRag.Tests.Domain;

/// <summary>
/// Unit-тесты жизненного цикла Repository: Active → Syncing → Indexing → Paused → Error → Deleting.
/// См. docs/testing.md §2 Domain, §5 Repository lifecycle и storage.md §2.
/// Использует реальный домейн CodeRag.Domain (Repository.Create + Mark*).
/// </summary>
public sealed class RepositoryLifecycleTests
{
    private static Repository CreateSut(
        string name = "demo",
        string url = "https://github.com/example/demo.git",
        string branch = "main",
        string localPath = "/data/repos/demo",
        RepositoryStatus status = RepositoryStatus.Active)
    {
        return Repository.Create(
            id: Guid.NewGuid(),
            name: name,
            gitUrl: url,
            branch: branch,
            localPath: localPath,
            status: status);
    }

    [Fact]
    public void New_repository_should_be_Active()
    {
        var repo = CreateSut();

        repo.Status.Should().Be(RepositoryStatus.Active);
        repo.Enabled.Should().BeTrue();
        repo.LastError.Should().BeNull();
        repo.CurrentCommit.Should().BeNull();
        repo.IndexedCommit.Should().BeNull();
        repo.GitUrl.Value.Should().Be("https://github.com/example/demo.git");
        repo.LocalPath.Value.Should().Be("/data/repos/demo");
    }

    [Fact]
    public void Full_lifecycle_Active_Syncing_Indexing_Paused_Error_Deleting()
    {
        var repo = CreateSut(name: "full", url: "https://github.com/acme/full.git", localPath: "/data/repos/full");

        // Active → Syncing
        repo.MarkSyncing();
        repo.Status.Should().Be(RepositoryStatus.Syncing);
        repo.LastError.Should().BeNull();

        // Syncing → Indexing (после fetch, обновляем CurrentCommit и переходим в Indexing)
        repo.UpdateCurrentCommit("abc1234");
        repo.LastSyncAt.Should().NotBeNull();
        repo.CurrentCommit.Should().Be("abc1234");

        repo.MarkIndexing();
        repo.Status.Should().Be(RepositoryStatus.Indexing);

        // Indexing → Paused (пользователь нажал pause во время индексации)
        repo.MarkPaused();
        repo.Status.Should().Be(RepositoryStatus.Paused);

        // Paused → Active (resume)
        repo.MarkActive();
        repo.Status.Should().Be(RepositoryStatus.Active);

        // Active → Indexing (ручной реиндекс)
        repo.MarkIndexing();
        repo.Status.Should().Be(RepositoryStatus.Indexing);

        // Indexing → Paused снова
        repo.MarkPaused();
        repo.Status.Should().Be(RepositoryStatus.Paused);
        repo.MarkActive();
        repo.Status.Should().Be(RepositoryStatus.Active);

        // Active → Syncing → Indexing снова
        repo.MarkSyncing();
        repo.UpdateCurrentCommit("def4567");
        repo.MarkIndexing();
        repo.Status.Should().Be(RepositoryStatus.Indexing);
        repo.CurrentCommit.Should().Be("def4567");

        // Indexing → Error (инжекция ошибки: network/auth fail, malformed source, embedding timeout)
        repo.MarkError("network/auth fail");
        repo.Status.Should().Be(RepositoryStatus.Error);
        repo.LastError.Should().Be("network/auth fail");

        // Error → Syncing (retry) — MarkSyncing очищает ошибку
        repo.MarkSyncing();
        repo.Status.Should().Be(RepositoryStatus.Syncing);
        repo.LastError.Should().BeNull();

        repo.UpdateCurrentCommit("def4567");
        repo.MarkIndexing();
        repo.Status.Should().Be(RepositoryStatus.Indexing);

        // Indexing → Active через MarkIndexSucceeded (инвариант IndexedCommit == CurrentCommit)
        repo.MarkIndexSucceeded("def4567");
        repo.Status.Should().Be(RepositoryStatus.Active);
        repo.IndexedCommit.Should().Be("def4567");
        repo.CurrentCommit.Should().Be("def4567");
        repo.IsIndexUpToDate().Should().BeTrue();
        repo.LastIndexAt.Should().NotBeNull();

        // Active → Error → Deleting
        repo.MarkError("embedding timeout");
        repo.Status.Should().Be(RepositoryStatus.Error);

        repo.MarkDeleting();
        repo.Status.Should().Be(RepositoryStatus.Deleting);
        // после MarkDeleting Enabled остаётся как был (Domain не сбрасывает Enabled, Infrastructure делает soft-delete)
        // но проверяем статус
    }

    [Fact]
    public void Delete_during_indexing_should_be_safe_and_idempotent()
    {
        var repo = CreateSut(url: "https://github.com/acme/del.git", localPath: "/data/repos/del");
        repo.MarkSyncing();
        repo.UpdateCurrentCommit("a1b2c3d");
        repo.MarkIndexing();
        repo.Status.Should().Be(RepositoryStatus.Indexing);

        // Delete во время индексации — безопасно (cancel job → каскад → idempotent) §5 testing.md
        repo.MarkDeleting();
        repo.Status.Should().Be(RepositoryStatus.Deleting);

        // повторный DELETE идемпотентен
        repo.MarkDeleting();
        repo.Status.Should().Be(RepositoryStatus.Deleting);
    }

    [Fact]
    public void Pause_from_Active_Syncing_Indexing_Error_is_allowed()
    {
        var cases = new[]
        {
            RepositoryStatus.Active,
            RepositoryStatus.Syncing,
            RepositoryStatus.Indexing,
            RepositoryStatus.Error
        };

        foreach (var start in cases)
        {
            var repo = CreateSut(name: $"pause-{start}", localPath: $"/data/repos/pause-{start.ToString().ToLower()}");

            // переводим в нужный старт
            switch (start)
            {
                case RepositoryStatus.Active: break;
                case RepositoryStatus.Syncing: repo.MarkSyncing(); break;
                case RepositoryStatus.Indexing: repo.MarkSyncing(); repo.MarkIndexing(); break;
                case RepositoryStatus.Error: repo.MarkError("e"); break;
            }

            repo.Status.Should().Be(start);
            repo.MarkPaused();
            repo.Status.Should().Be(RepositoryStatus.Paused);
        }
    }

    [Fact]
    public void ClearError_should_return_to_Active()
    {
        var repo = CreateSut(localPath: "/data/repos/clear");
        repo.MarkError("broken C# analyzer error");
        repo.Status.Should().Be(RepositoryStatus.Error);

        repo.ClearError();
        repo.Status.Should().Be(RepositoryStatus.Active);
        repo.LastError.Should().BeNull();
    }

    [Fact]
    public void Deleting_should_block_MarkSyncing_MarkIndexing_MarkError()
    {
        var repo = CreateSut(localPath: "/data/repos/block");
        repo.MarkDeleting();
        repo.Status.Should().Be(RepositoryStatus.Deleting);

        FluentActions.Invoking(() => repo.MarkSyncing())
            .Should().Throw<DomainValidationException>()
            .WithMessage("*being deleted*");

        FluentActions.Invoking(() => repo.MarkIndexing())
            .Should().Throw<DomainValidationException>();

        FluentActions.Invoking(() => repo.MarkError("e"))
            .Should().Throw<DomainValidationException>();

        FluentActions.Invoking(() => repo.MarkPaused())
            .Should().Throw<DomainValidationException>();

        // MarkDeleting itself идемпотентен — не бросает
        FluentActions.Invoking(() => repo.MarkDeleting())
            .Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_should_validate_name(string badName)
    {
        var act = () => Repository.Create(Guid.NewGuid(), badName, "https://github.com/a/b.git", "main", "/data/repos/a");
        act.Should().Throw<DomainValidationException>();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/repo.git")]
    [InlineData("")]
    public void Create_should_validate_git_url(string badUrl)
    {
        var act = () => Repository.Create(Guid.NewGuid(), "demo", badUrl, "main", "/data/repos/demo");
        act.Should().Throw<DomainValidationException>()
            .WithMessage("*GitUrl*");
    }

    [Theory]
    [InlineData("/tmp/repos/demo")]
    [InlineData("relative/path")]
    [InlineData("/data/other/demo")]
    public void Create_should_validate_localPath(string badPath)
    {
        var act = () => Repository.Create(Guid.NewGuid(), "demo", "https://github.com/a/b.git", "main", badPath);
        act.Should().Throw<DomainValidationException>()
            .WithMessage("*LocalPath*");
    }

    [Fact]
    public void MarkIndexSucceeded_sets_both_commits_and_Active()
    {
        var repo = CreateSut(localPath: "/data/repos/idx");
        repo.MarkSyncing();
        repo.UpdateCurrentCommit("abc1234");

        repo.MarkIndexing();
        repo.MarkIndexSucceeded("abc1234");

        repo.Status.Should().Be(RepositoryStatus.Active);
        repo.IndexedCommit.Should().Be("abc1234");
        repo.CurrentCommit.Should().Be("abc1234");
        repo.IsIndexUpToDate().Should().BeTrue();
    }

    [Fact]
    public void MarkError_and_ClearError_lifecycle()
    {
        var repo = CreateSut(localPath: "/data/repos/err");
        repo.MarkError("broken C# analyzer error");

        repo.Status.Should().Be(RepositoryStatus.Error);
        repo.LastError.Should().Be("broken C# analyzer error");

        repo.ClearError();
        repo.Status.Should().Be(RepositoryStatus.Active);
        repo.LastError.Should().BeNull();
    }

    [Fact]
    public void Analyzer_CanAnalyze_mock_example_with_NSubstitute()
    {
        // Пример использования NSubstitute для анализаторов (docs/testing.md §2)
        var analyzer = Substitute.For<ICodeAnalyzer>();
        analyzer.Language.Returns("csharp");
        analyzer.CanAnalyze(Arg.Any<string>()).Returns(call => ((string)call[0]).EndsWith(".cs"));

        analyzer.CanAnalyze("src/Foo.cs").Should().BeTrue();
        analyzer.CanAnalyze("src/app.py").Should().BeFalse();
        analyzer.CanAnalyze("tests/fixtures/csharp/Sample.cs").Should().BeTrue();

        analyzer.Received().CanAnalyze("src/Foo.cs");
    }

    [Fact]
    public async Task Analyzer_AnalyzeAsync_mock_with_FileContext()
    {
        var analyzer = Substitute.For<ICodeAnalyzer>();
        analyzer.Language.Returns("csharp");
        analyzer.CanAnalyze(Arg.Any<string>()).Returns(true);
        analyzer.AnalyzeAsync(Arg.Any<FileContext>(), Arg.Any<CancellationToken>())
            .Returns(AnalyzeResult.Empty);

        var ctx = new FileContext(
            RepositoryId: Guid.NewGuid(),
            ProjectId: null,
            FilePath: "tests/fixtures/csharp/Sample.cs",
            Content: "// sample",
            Language: "csharp");

        var result = await analyzer.AnalyzeAsync(ctx, CancellationToken.None);

        result.Should().NotBeNull();
        result.Symbols.Should().BeEmpty();
        result.Chunks.Should().BeEmpty();

        await analyzer.Received(1).AnalyzeAsync(Arg.Is<FileContext>(c => c.FilePath == ctx.FilePath), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GitUrl_ValueObject_validation()
    {
        FluentActions.Invoking(() => CodeRag.Domain.ValueObjects.GitUrl.Create("not-a-url"))
            .Should().Throw<DomainValidationException>();

        var ok = CodeRag.Domain.ValueObjects.GitUrl.Create("https://github.com/org/repo.git");
        ok.Value.Should().Be("https://github.com/org/repo.git");

        var scp = CodeRag.Domain.ValueObjects.GitUrl.Create("git@github.com:org/repo.git");
        scp.Value.Should().Be("git@github.com:org/repo.git");
    }
}
