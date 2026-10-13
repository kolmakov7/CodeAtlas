using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Stores;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Doc = CodeRag.Domain.Entities.Documentation;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Persistence-level tests for <see cref="DocumentationStore"/>: round-trip, stable-identity
/// upsert (same target, new id updates the existing row keeping its id), listing, staleness flip
/// and deletion.
/// </summary>
public sealed class DocumentationStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly DocumentationStore _store;

    public DocumentationStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        var dbPath = Path.Combine(_dataDir, "coderag.db");
        _factory = new SqliteConnectionFactory(dbPath, NullLogger<SqliteConnectionFactory>.Instance);
        _store = new DocumentationStore(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task EnsureMigratedAsync()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();
    }

    private async Task SeedRepoAsync(Guid repo)
    {
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s) ON CONFLICT(id) DO NOTHING",
            new { i = repo.ToString(), n = "t" + repo, g = "file:///x", p = "/x", s = "ready" });
    }

    [Fact]
    public async Task Upsert_then_GetByTarget_round_trips_content_and_metadata()
    {
        await EnsureMigratedAsync();
        var repo = Guid.NewGuid();
        await SeedRepoAsync(repo);

        var id = Guid.NewGuid();
        var doc = Doc.Create(id, repo, DocumentationTargetType.Symbol, "NS.A", "A",
            targetId: Guid.NewGuid(), filePath: "src/A.cs", language: "csharp",
            content: "# A\n\nBody.", sourceHash: "h1", model: "gpt", promptVersion: "v1",
            contextChars: 100, outputChars: 10);
        await _store.UpsertAsync(doc);

        var byTarget = await _store.GetByTargetAsync(repo, DocumentationTargetType.Symbol, "NS.A");
        byTarget.Should().NotBeNull();
        byTarget!.Id.Should().Be(id);
        byTarget.Content.Should().Be("# A\n\nBody.");
        byTarget.TargetRef.Should().Be("NS.A");
        byTarget.FilePath.Should().Be("src/A.cs");
        byTarget.SourceHash.Should().Be("h1");
        byTarget.Model.Should().Be("gpt");
        byTarget.ContextChars.Should().Be(100);
        byTarget.Status.Should().Be(DocumentationStatus.Generated);

        var byId = await _store.GetByIdAsync(id);
        byId.Should().NotBeNull();
        byId!.Content.Should().Be("# A\n\nBody.");
    }

    [Fact]
    public async Task Upsert_same_target_new_id_updates_existing_row_and_keeps_id()
    {
        await EnsureMigratedAsync();
        var repo = Guid.NewGuid();
        await SeedRepoAsync(repo);

        var id1 = Guid.NewGuid();
        await _store.UpsertAsync(Doc.Create(id1, repo, DocumentationTargetType.Symbol, "NS.A", "A", content: "v1"));

        var id2 = Guid.NewGuid();
        await _store.UpsertAsync(Doc.Create(id2, repo, DocumentationTargetType.Symbol, "NS.A", "A", content: "v2"));

        var byId1 = await _store.GetByIdAsync(id1);
        byId1.Should().NotBeNull();
        byId1!.Content.Should().Be("v2"); // updated in place, original id preserved

        var byId2 = await _store.GetByIdAsync(id2);
        byId2.Should().BeNull(); // the second id was never used as the primary key

        (await _store.GetByRepositoryAsync(repo)).Count.Should().Be(1);
    }

    [Fact]
    public async Task GetByRepositoryAndType_filters_by_type()
    {
        await EnsureMigratedAsync();
        var repo = Guid.NewGuid();
        await SeedRepoAsync(repo);

        await _store.UpsertAsync(Doc.Create(Guid.NewGuid(), repo, DocumentationTargetType.Symbol, "NS.A", "A", content: "a"));
        await _store.UpsertAsync(Doc.Create(Guid.NewGuid(), repo, DocumentationTargetType.Repository, "repository", "repo", content: "r"));
        await _store.UpsertAsync(Doc.Create(Guid.NewGuid(), repo, DocumentationTargetType.Symbol, "NS.B", "B", content: "b"));

        var symbols = await _store.GetByRepositoryAndTypeAsync(repo, DocumentationTargetType.Symbol);
        symbols.Select(d => d.Name).Should().BeEquivalentTo("A", "B");

        var all = await _store.GetByRepositoryAsync(repo);
        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task MarkStaleByRepository_flips_in_progress_but_not_generated_or_failed()
    {
        await EnsureMigratedAsync();
        var repo = Guid.NewGuid();
        await SeedRepoAsync(repo);

        await _store.UpsertAsync(Doc.Create(Guid.NewGuid(), repo, DocumentationTargetType.Symbol, "NS.A", "A",
            status: DocumentationStatus.Generating, content: "a"));
        await _store.UpsertAsync(Doc.Create(Guid.NewGuid(), repo, DocumentationTargetType.Symbol, "NS.B", "B",
            status: DocumentationStatus.Generated, content: "b"));
        await _store.UpsertAsync(Doc.Create(Guid.NewGuid(), repo, DocumentationTargetType.Symbol, "NS.C", "C",
            status: DocumentationStatus.Failed, content: "c", error: "boom"));

        await _store.MarkStaleByRepositoryAsync(repo);

        var docs = (await _store.GetByRepositoryAsync(repo)).ToDictionary(d => d.Name, d => d.Status);
        docs["A"].Should().Be(DocumentationStatus.Stale);
        docs["B"].Should().Be(DocumentationStatus.Generated);
        docs["C"].Should().Be(DocumentationStatus.Failed);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_row()
    {
        await EnsureMigratedAsync();
        var repo = Guid.NewGuid();
        await SeedRepoAsync(repo);

        var id = Guid.NewGuid();
        await _store.UpsertAsync(Doc.Create(id, repo, DocumentationTargetType.Symbol, "NS.A", "A", content: "a"));
        (await _store.GetByRepositoryAsync(repo)).Should().HaveCount(1);

        await _store.DeleteAsync(id);

        (await _store.GetByIdAsync(id)).Should().BeNull();
        (await _store.GetByRepositoryAsync(repo)).Should().BeEmpty();
    }
}
