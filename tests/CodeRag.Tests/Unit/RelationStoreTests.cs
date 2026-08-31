using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Stores;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Persistence-level regression tests for <see cref="RelationStore"/>:
/// stale-relation removal on a language replace (the C# reindex path) and
/// direction-aware lookup (reverse references).
/// </summary>
public sealed class RelationStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly RelationStore _store;

    public RelationStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-relations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        var dbPath = Path.Combine(_dataDir, "coderag.db");
        _factory = new SqliteConnectionFactory(dbPath, NullLogger<SqliteConnectionFactory>.Instance);
        _store = new RelationStore(_factory);
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

    /// <summary>Seed repository + file so that symbols (and therefore relation endpoints) satisfy FKs.</summary>
    private async Task SeedRepoFileAsync(Guid repo, Guid file)
    {
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s) ON CONFLICT(id) DO NOTHING",
            new { i = repo.ToString(), n = "t" + repo, g = "file:///x", p = "/x", s = "ready" });
        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path) VALUES(@i,@r,@p) ON CONFLICT(id) DO NOTHING",
            new { i = file.ToString(), r = repo.ToString(), p = "C.cs" });
    }

    private async Task SeedSymbolAsync(Guid repo, Guid file, Guid symbol, string name, string kind = "Method")
    {
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name) VALUES(@i,@r,@f,@l,@k,@n) ON CONFLICT(id) DO NOTHING",
            new { i = symbol.ToString(), r = repo.ToString(), f = file.ToString(), l = "csharp", k = kind, n = name });
    }

    [Fact]
    public async Task ReplaceForLanguage_removes_stale_relations_on_reindex()
    {
        await EnsureMigratedAsync();

        var repo = Guid.NewGuid();
        var file = Guid.NewGuid();
        var a = Guid.NewGuid(); // Service type
        var b = Guid.NewGuid(); // Execute method
        var c = Guid.NewGuid(); // IRepository.Save method (the callee that will go away)
        var d = Guid.NewGuid(); // IService interface
        await SeedRepoFileAsync(repo, file);
        await SeedSymbolAsync(repo, file, a, "Service", "Class");
        await SeedSymbolAsync(repo, file, b, "Execute");
        await SeedSymbolAsync(repo, file, c, "Save");
        await SeedSymbolAsync(repo, file, d, "IService", "Interface");

        // First index: Execute calls c, Service contains b, Service implements d.
        await _store.ReplaceForLanguageAsync(repo, "csharp", new[]
        {
            new Relation(b, c, RelationType.Calls),
            new Relation(a, b, RelationType.Contains),
            new Relation(a, d, RelationType.Implements),
        });
        (await _store.GetByRepositoryAsync(repo)).Should().HaveCount(3);

        // Reindex after `Execute` no longer calls c: the stale edge must disappear.
        await _store.ReplaceForLanguageAsync(repo, "csharp", new[]
        {
            new Relation(a, b, RelationType.Contains),
            new Relation(a, d, RelationType.Implements),
        });

        var remaining = await _store.GetByRepositoryAsync(repo);
        remaining.Should().HaveCount(2);
        remaining.Should().NotContain(r => r.FromSymbolId == b && r.ToSymbolId == c && r.RelationType == RelationType.Calls);

        // Upserting the same edge again is idempotent (PK = from+to+type).
        await _store.UpsertBatchAsync(repo, new[] { new Relation(a, b, RelationType.Contains) });
        (await _store.GetByRepositoryAsync(repo)).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetBySymbol_filters_by_direction()
    {
        await EnsureMigratedAsync();

        var repo = Guid.NewGuid();
        var file = Guid.NewGuid();
        var caller = Guid.NewGuid();   // method that calls
        var callee = Guid.NewGuid();   // method that is called
        var dependent = Guid.NewGuid(); // type that depends on target
        var target = Guid.NewGuid();   // the type under inspection
        await SeedRepoFileAsync(repo, file);
        await SeedSymbolAsync(repo, file, caller, "Caller");
        await SeedSymbolAsync(repo, file, callee, "Callee");
        await SeedSymbolAsync(repo, file, dependent, "Dependent", "Class");
        await SeedSymbolAsync(repo, file, target, "Target", "Class");

        await _store.UpsertBatchAsync(repo, new[]
        {
            new Relation(caller, target, RelationType.Calls),     // incoming to target
            new Relation(target, callee, RelationType.Calls),     // outgoing from target
            new Relation(dependent, target, RelationType.DependsOn), // incoming to target
        });

        var outgoing = await _store.GetBySymbolAsync(target, RelationDirection.Outgoing);
        outgoing.Should().ContainSingle().Which.RelationType.Should().Be(RelationType.Calls);

        var incoming = await _store.GetBySymbolAsync(target, RelationDirection.Incoming);
        incoming.Should().HaveCount(2);
        incoming.Should().Contain(r => r.RelationType == RelationType.Calls && r.FromSymbolId == caller);
        incoming.Should().Contain(r => r.RelationType == RelationType.DependsOn && r.FromSymbolId == dependent);

        var both = await _store.GetBySymbolAsync(target, RelationDirection.Both);
        both.Should().HaveCount(3);

        // Type filter narrows the set.
        var onlyDepends = await _store.GetBySymbolAsync(target, RelationDirection.Incoming, repo, new[] { RelationType.DependsOn });
        onlyDepends.Should().ContainSingle().Which.FromSymbolId.Should().Be(dependent);
    }
}
