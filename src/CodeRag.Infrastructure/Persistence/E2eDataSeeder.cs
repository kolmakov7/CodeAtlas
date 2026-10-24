using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Seeds a tiny, deterministic dataset for browser (Playwright) end-to-end tests when
/// CODERAG__E2E_SEED=1. Creates two linked repositories (an app + a DB) with a handful of
/// files/symbols/relations/usage so the UI can be exercised without the real index.
/// Idempotent: wipes previous seed rows first (repositories cascade to files/symbols/relations).
/// </summary>
public sealed class E2eDataSeeder
{
    // Fixed ids for stable assertions.
    public const string AppRepoId = "11111111-1111-1111-1111-111111111111";
    public const string DbRepoId = "22222222-2222-2222-2222-222222222222";
    public const string AppFileId = "aaaa1111-1111-1111-1111-111111111111";
    public const string DbFileId = "bbbb2222-2222-2222-2222-222222222222";
    public const string AppSymbolId = "a0a0a0a0-0000-0000-0000-000000000001";
    public const string DbSymbolId = "b0b0b0b0-0000-0000-0000-000000000002";
    public const string DbTableSymbolId = "b0b0b0b0-0000-0000-0000-000000000003";

    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<E2eDataSeeder> _logger;

    public E2eDataSeeder(IDbConnectionFactory factory, ILogger<E2eDataSeeder> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("o");
        const string group = "00000000-0000-0000-0000-000000000001";

        // Clean previous seed (cascades to files/symbols/relations/usage via FKs where present).
        await conn.ExecuteAsync(
            "DELETE FROM repositories WHERE name IN ('e2e-app','e2e-db');").ConfigureAwait(false);

        await conn.ExecuteAsync(@"
INSERT INTO repositories(id,name,git_url,branch,local_path,status,enabled,group_id,filter_enabled,current_commit,indexed_commit,last_index_at)
VALUES (@id,'e2e-app','https://example.com/e2e-app.git','main','/tmp/coderag-e2e/repos/e2e-app','Active',0,@grp,0,'deadbee','deadbee',@now),
       (@id2,'e2e-db','https://example.com/e2e-db.git','master','/tmp/coderag-e2e/repos/e2e-db','Active',0,@grp,0,'deadbee','deadbee',@now);",
            new { id = AppRepoId, id2 = DbRepoId, grp = group, now }).ConfigureAwait(false);

        await conn.ExecuteAsync(@"
INSERT INTO files(id,repository_id,path,language,hash,last_indexed_at)
VALUES (@fa,@ra,'src/App/Caller.cs','csharp','hash-app',@now),
       (@fb,@rb,'schema/Target.sql','sql','hash-db',@now);",
            new { fa = AppFileId, fb = DbFileId, ra = AppRepoId, rb = DbRepoId, now }).ConfigureAwait(false);

        await conn.ExecuteAsync(@"
INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn,namespace,start_line,end_line,signature)
VALUES (@sa,@ra,@fa,'csharp','Class','Caller','E2e.App.Caller','E2e.App',1,40,'class Caller'),
       (@sb,@rb,@fb,'sql','Procedure','TargetProc','dbo.TargetProc','dbo',1,120,'Procedure dbo.TargetProc'),
       (@st,@rb,@fb,'sql','Table','TargetTable','dbo.TargetTable','dbo',1,20,'Table dbo.TargetTable');",
            new { sa = AppSymbolId, sb = DbSymbolId, st = DbTableSymbolId, ra = AppRepoId, rb = DbRepoId, fa = AppFileId, fb = DbFileId }).ConfigureAwait(false);

        // Cross-repository relation: app Caller -> db TargetProc (origin = app). Demonstrates that
        // callers in a linked repository are surfaced (SymbolExplorer "Callers", doc context).
        await conn.ExecuteAsync(@"
INSERT INTO relations(from_symbol_id,to_symbol_id,relation_type,repository_id,from_repository_id,to_repository_id,origin_repository_id)
VALUES (@caller,@target,'Calls',@app,@app,@db,@app);",
            new { caller = AppSymbolId, target = DbSymbolId, app = AppRepoId, db = DbRepoId }).ConfigureAwait(false);

        _logger.LogInformation("E2E seed applied: 2 repositories, 3 symbols, 1 cross-repo relation");
    }
}
