using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class SymbolStore : CodeRag.Domain.Ports.ISymbolStore, ISymbolStore
{
    private readonly IDbConnectionFactory _factory;
    public SymbolStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Symbol symbol, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await ExecuteUpsertAsync(conn, null, symbol).ConfigureAwait(false);
    }

    public async Task UpsertBatchAsync(IEnumerable<Symbol> symbols, CancellationToken ct = default)
    {
        var list = symbols.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var s in list) await ExecuteUpsertAsync(conn, tx, s).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task ExecuteUpsertAsync(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx, Symbol s)
    {
        const string sql = @"
INSERT INTO symbols(id, repository_id, project_id, file_id, language, kind, name, fqn, namespace, parent_symbol_id, start_line, end_line, start_col, end_col, signature)
VALUES (@id,@repo,@proj,@file,@lang,@kind,@name,@fqn,@ns,@parent,@sl,@el,@sc,@ec,@sig)
ON CONFLICT(id) DO UPDATE SET
  repository_id=excluded.repository_id, project_id=excluded.project_id, file_id=excluded.file_id,
  language=excluded.language, kind=excluded.kind, name=excluded.name, fqn=excluded.fqn, namespace=excluded.namespace,
  parent_symbol_id=excluded.parent_symbol_id, start_line=excluded.start_line, end_line=excluded.end_line,
  start_col=excluded.start_col, end_col=excluded.end_col, signature=excluded.signature;";

        await conn.ExecuteAsync(sql, new
        {
            id = s.Id.ToString(),
            repo = s.RepositoryId.ToString(),
            proj = s.ProjectId?.ToString(),
            file = s.FileId.ToString(),
            lang = s.Language,
            kind = s.Kind.ToString(),
            name = s.Name,
            fqn = s.Fqn,
            ns = s.Namespace,
            parent = s.ParentSymbolId?.ToString(),
            sl = s.StartLine,
            el = s.EndLine,
            sc = s.StartCol,
            ec = s.EndCol,
            sig = s.Signature
        }, transaction: tx).ConfigureAwait(false);
    }

    public async Task<Symbol?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SymRow>("SELECT * FROM symbols WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Symbol>> GetByFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SymRow>("SELECT * FROM symbols WHERE file_id=@fid ORDER BY start_line;", new { fid = fileId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Symbol>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SymRow>("SELECT * FROM symbols WHERE repository_id=@repo ORDER BY fqn;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteByFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM symbols WHERE file_id=@fid;", new { fid = fileId.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM symbols WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
    }

    private static Symbol Map(SymRow r)
    {
        return Symbol.Create(
            Guid.Parse(r.id),
            Guid.Parse(r.repository_id),
            Guid.Parse(r.file_id),
            r.language,
            Enum.TryParse<SymbolKind>(r.kind, out var k) ? k : SymbolKind.Class,
            r.name,
            r.fqn,
            r.@namespace,
            string.IsNullOrWhiteSpace(r.parent_symbol_id) ? null : Guid.Parse(r.parent_symbol_id),
            string.IsNullOrWhiteSpace(r.project_id) ? null : Guid.Parse(r.project_id),
            r.start_line,
            r.end_line,
            r.start_col,
            r.end_col,
            r.signature);
    }

    private sealed class SymRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string? project_id { get; set; }
        public string file_id { get; set; } = "";
        public string language { get; set; } = "";
        public string kind { get; set; } = "";
        public string name { get; set; } = "";
        public string? fqn { get; set; }
        public string? @namespace { get; set; }
        public string? parent_symbol_id { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public int? start_col { get; set; }
        public int? end_col { get; set; }
        public string? signature { get; set; }
    }
}
