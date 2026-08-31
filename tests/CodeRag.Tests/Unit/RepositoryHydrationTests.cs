using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class RepositoryHydrationTests
{
    [Fact]
    public void Rehydrate_stale_commits_should_preserve_stale_state()
    {
        var id = Guid.NewGuid();
        var repo = Repository.Rehydrate(
            id, "demo", "https://github.com/org/demo.git", "main", "/data/repos/demo",
            currentCommit: "abc1234567890abc1234567890abc1234567890abcd",
            indexedCommit: "def1234567890abc1234567890abc1234567890abcd",
            status: RepositoryStatus.Active, enabled: true,
            proxyId: null, credentialId: null, syncIntervalSeconds: 300,
            lastSyncAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastIndexAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            lastError: null);

        repo.CurrentCommit.Should().Be("abc1234567890abc1234567890abc1234567890abcd");
        repo.IndexedCommit.Should().Be("def1234567890abc1234567890abc1234567890abcd");
        repo.IsIndexUpToDate().Should().BeFalse();
        // AssertIndexInvariant should throw for stale
        FluentActions.Invoking(() => repo.AssertIndexInvariant())
            .Should().Throw<CodeRag.Domain.Exceptions.DomainValidationException>();
    }

    [Fact]
    public void Rehydrate_up_to_date_should_be_healthy()
    {
        var sha = "aaaabbbbccccddddeeeeffffaaaabbbbccccdddd";
        var repo = Repository.Rehydrate(Guid.NewGuid(), "demo2", "https://github.com/org/demo2.git", "main", "/data/repos/demo2",
            currentCommit: sha, indexedCommit: sha, status: RepositoryStatus.Active, enabled: true,
            null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        repo.IsIndexUpToDate().Should().BeTrue();
        FluentActions.Invoking(() => repo.AssertIndexInvariant()).Should().NotThrow();
    }

    [Fact]
    public void Rehydrate_null_commits_should_not_throw()
    {
        var repo = Repository.Rehydrate(Guid.NewGuid(), "demo3", "https://github.com/org/demo3.git", "main", "/data/repos/demo3",
            null, null, RepositoryStatus.Active, true, null, null, null, null, null, null);
        repo.CurrentCommit.Should().BeNull();
        repo.IndexedCommit.Should().BeNull();
        repo.IsIndexUpToDate().Should().BeFalse();
        FluentActions.Invoking(() => repo.AssertIndexInvariant()).Should().NotThrow();
    }

    [Fact]
    public void Chunk_Rehydrate_preserves_content_hash()
    {
        var id = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var source = "Console.WriteLine(\"hi\");";
        var hash = Chunk.ComputeHash(source);
        var chunk = Chunk.Rehydrate(id, repoId, fileId, source, hash, null, "csharp", 1, 1, "model", "cfg", DateTimeOffset.UtcNow);
        chunk.ContentHash.Should().Be(hash);
        chunk.Source.Should().Be(source);

        // legacy hash different
        var legacyHash = new string('a', 64);
        var chunk2 = Chunk.Rehydrate(id, repoId, fileId, source, legacyHash, null, "csharp", 1, 1, null, null, null);
        chunk2.ContentHash.Should().Be(legacyHash);
    }
}
