using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Stores;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeRag.Tests.Unit;

/// <summary>Persistence tests for repository links and cross-repo relation ownership.</summary>
public sealed class RepositoryLinkStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly RepositoryLinkStore _links;
    private readonly RelationStore _relations;

    public RepositoryLinkStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);
        _links = new RepositoryLinkStore(_factory);
        _relations = new RelationStore(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<Guid> SeedRepoAsync(string name)
    {
        var id = Guid.NewGuid();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync("INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,'file:///x','/x','ready')",
            new { i = id.ToString(), n = name });
        return id;
    }

    private async Task<Guid> SeedSymbolAsync(Guid repo, string name)
    {
        var fileId = Guid.NewGuid();
        var symbolId = Guid.NewGuid();
        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync("INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,'sql')",
            new { i = fileId.ToString(), r = repo.ToString(), p = $"src/{name}.sql" });
        await conn.ExecuteAsync(@"INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn,start_line,end_line)
VALUES(@i,@r,@f,'sql','Procedure',@n,@fqn,1,10)", new { i = symbolId.ToString(), r = repo.ToString(), f = fileId.ToString(), n = name, fqn = "dbo." + name });
        return symbolId;
    }

    [Fact]
    public async Task Create_list_and_delete_round_trips()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        var app = await SeedRepoAsync("app");
        var db = await SeedRepoAsync("db");
        var link = RepositoryLink.Create(Guid.NewGuid(), app, db, "depends_on", note: "app calls db");

        await _links.UpsertAsync(link);

        var byRepo = await _links.GetByRepositoryAsync(app);
        byRepo.Should().ContainSingle().Which.TargetRepositoryId.Should().Be(db);
        (await _links.GetByRepositoryAsync(db)).Should().ContainSingle();

        await _links.DeleteAsync(link.Id);
        (await _links.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_is_idempotent_on_same_pair_and_type()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        var app = await SeedRepoAsync("app");
        var db = await SeedRepoAsync("db");
        await _links.UpsertAsync(RepositoryLink.Create(Guid.NewGuid(), app, db, "calls"));
        await _links.UpsertAsync(RepositoryLink.Create(Guid.NewGuid(), app, db, "calls"));

        (await _links.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Cross_repo_edge_is_owned_by_origin_and_not_deleted_by_target_reindex()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        var app = await SeedRepoAsync("app");
        var db = await SeedRepoAsync("db");
        var caller = await SeedSymbolAsync(app, "Caller");
        var target = await SeedSymbolAsync(db, "TargetProc");

        // app -> db cross-repo edge, originated by app.
        await _relations.UpsertBatchAsync(app, new[] { new Relation(caller, target, RelationType.Calls) });

        // Re-indexing db (replace its own language edges) must NOT delete app's edge.
        await _relations.ReplaceForLanguageAsync(db, "sql", Array.Empty<Relation>());
        (await _relations.GetBySymbolAsync(target, RelationDirection.Incoming)).Should().ContainSingle();

        // The edge is visible from both repositories (endpoint ownership).
        (await _relations.GetByRepositoryAsync(db)).Should().ContainSingle();

        // Origin-scoped cross-repo cleanup removes it.
        await _relations.DeleteCrossRepoByOriginAsync(app);
        (await _relations.GetBySymbolAsync(target, RelationDirection.Incoming)).Should().BeEmpty();
    }
}
