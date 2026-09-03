using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;
using System.Data;
using System.Text;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Stores;

public sealed class SymbolStore : CodeRag.Domain.Ports.ISymbolStore
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

    private async Task ExecuteUpsertAsync(IDbConnection conn, IDbTransaction? tx, Symbol s)
    {
        var ns = SqlDialect.QuoteIdent(_factory, "namespace");
        var sql = $@"
 INSERT INTO symbols(id, repository_id, project_id, file_id, language, kind, name, fqn, {ns}, parent_symbol_id, start_line, end_line, start_col, end_col, signature)
 VALUES (@id,@repo,@proj,@file,@lang,@kind,@name,@fqn,@ns,@parent,@sl,@el,@sc,@ec,@sig)
 ON CONFLICT(id) DO UPDATE SET
   repository_id=excluded.repository_id, project_id=excluded.project_id, file_id=excluded.file_id,
   language=excluded.language, kind=excluded.kind, name=excluded.name, fqn=excluded.fqn, {ns}=excluded.{ns},
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

    public async Task<IReadOnlyCollection<Guid>> FindExactIdsAsync(
        string name,
        Guid[]? repositoryIds = null,
        string[]? languages = null,
        string? kind = null,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var sql = new StringBuilder("SELECT id FROM symbols WHERE (fqn = @q OR name = @q) ");
        var p = new DynamicParameters();
        p.Add("q", name);
        if (repositoryIds != null && repositoryIds.Length > 0)
        {
            var ids = repositoryIds.Select(g => g.ToString()).ToArray();
            sql.Append($" AND repository_id IN ({string.Join(",", ids.Select((_, i) => $"@r{i}"))}) ");
            for (int i = 0; i < ids.Length; i++) p.Add($"r{i}", ids[i]);
        }
        if (languages != null && languages.Length > 0)
        {
            sql.Append($" AND language IN ({string.Join(",", languages.Select((_, i) => $"@l{i}"))}) ");
            for (int i = 0; i < languages.Length; i++) p.Add($"l{i}", languages[i]);
        }
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sql.Append(" AND kind = @kind ");
            p.Add("kind", kind);
        }
        var rows = (await conn.QueryAsync<string>(sql.ToString(), p).ConfigureAwait(false)).ToList();
        var result = new List<Guid>(rows.Count);
        foreach (var r in rows) if (Guid.TryParse(r, out var g)) result.Add(g);
        return result;
    }

    public async Task<IReadOnlyCollection<Guid>> FindLikeIdsAsync(
        string term,
        Guid[]? repositoryIds = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var where = $"({SqlDialect.NoCaseLike(_factory, "name", "@like")} OR {SqlDialect.NoCaseLike(_factory, "fqn", "@like")}) ";
        var sql = new StringBuilder($"SELECT id FROM symbols WHERE {where}");
        var p = new DynamicParameters();
        p.Add("like", $"%{EscapeLike(term)}%");
        p.Add("limit", limit);
        if (repositoryIds != null && repositoryIds.Length > 0)
        {
            var ids = repositoryIds.Select(g => g.ToString()).ToArray();
            sql.Append($" AND repository_id IN ({string.Join(",", ids.Select((_, i) => $"@r{i}"))}) ");
            for (int i = 0; i < ids.Length; i++) p.Add($"r{i}", ids[i]);
        }
        sql.Append(" LIMIT @limit;");
        var rows = (await conn.QueryAsync<string>(sql.ToString(), p).ConfigureAwait(false)).ToList();
        var result = new List<Guid>(rows.Count);
        foreach (var r in rows) if (Guid.TryParse(r, out var g)) result.Add(g);
        return result;
    }

    public async Task<IReadOnlyList<SymbolInfo>> GetSearchInfoByIdsAsync(IReadOnlyCollection<Guid> symbolIds, CancellationToken ct = default)
    {
        if (symbolIds.Count == 0) return Array.Empty<SymbolInfo>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ids = symbolIds.Select(g => g.ToString()).ToArray();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"id{i}", ids[i]);
        var sql = SymbolInfoSql(_factory) + $" WHERE s.id IN ({placeholders});";
        var rows = (await conn.QueryAsync<SymInfoRow>(sql, p).ConfigureAwait(false)).ToList();
        return rows.Select(ToInfo).ToList();
    }

    public async Task<IReadOnlyList<SymbolInfo>> SearchAsync(
        string term,
        Guid? repositoryId = null,
        string? language = null,
        string? kind = null,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var q = term.Trim();
        var ns = "s." + SqlDialect.QuoteIdent(_factory, "namespace");
        var where = $"({SqlDialect.NoCaseLike(_factory, "s.name", "@like")} OR {SqlDialect.NoCaseLike(_factory, "s.fqn", "@like")} OR {SqlDialect.NoCaseLike(_factory, ns, "@like")}) ";
        var sql = new StringBuilder(SymbolInfoSql(_factory) + $" WHERE {where}");
        var p = new DynamicParameters();
        p.Add("like", $"%{EscapeLike(q)}%");
        p.Add("limit", limit);
        p.Add("offset", offset);
        if (repositoryId.HasValue) { sql.Append(" AND s.repository_id = @repo "); p.Add("repo", repositoryId.Value.ToString()); }
        if (!string.IsNullOrWhiteSpace(language)) { sql.Append(" AND s.language = @lang "); p.Add("lang", language.Trim()); }
        if (!string.IsNullOrWhiteSpace(kind)) { sql.Append(" AND s.kind = @kind "); p.Add("kind", kind.Trim()); }
        p.Add("exact", q);
        var kindOrder = new[] { SymbolKind.Class, SymbolKind.Interface, SymbolKind.Record, SymbolKind.Struct, SymbolKind.Enum, SymbolKind.Method };
        for (int i = 0; i < kindOrder.Length; i++) p.Add($"k{i}", kindOrder[i].ToString());
        var kindCase = string.Join(" ", kindOrder.Select((k, i) => $"WHEN @k{i} THEN {i}"));
        var exactName = SqlDialect.NoCaseEq(_factory, "s.name", "@exact");
        var exactFqn = SqlDialect.NoCaseEq(_factory, "s.fqn", "@exact");
        sql.Append($" ORDER BY CASE WHEN {exactName} THEN 0 WHEN {exactFqn} THEN 1 ELSE 2 END, CASE s.kind {kindCase} ELSE 6 END, length(s.fqn), s.fqn LIMIT @limit OFFSET @offset;");
        var rows = (await conn.QueryAsync<SymInfoRow>(sql.ToString(), p).ConfigureAwait(false)).ToList();
        return rows.Select(ToInfo).ToList();
    }

    private static string SymbolInfoSql(IDbConnectionFactory factory)
    {
        var ns = "s." + SqlDialect.QuoteIdent(factory, "namespace");
        return $"SELECT s.id, s.repository_id, s.file_id, s.language, s.kind, s.name, s.fqn, {ns}, s.signature, s.start_line, s.end_line, f.path as file_path FROM symbols s JOIN files f ON f.id = s.file_id";
    }

    private static SymbolInfo ToInfo(SymInfoRow r) => new(Guid.Parse(r.id), Guid.Parse(r.repository_id), Guid.Parse(r.file_id), r.language, r.kind, r.name, r.fqn, r.@namespace, r.signature, r.start_line, r.end_line, r.file_path ?? string.Empty);
    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

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

    private sealed class SymInfoRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string file_id { get; set; } = "";
        public string language { get; set; } = "";
        public string kind { get; set; } = "";
        public string name { get; set; } = "";
        public string? fqn { get; set; }
        public string? @namespace { get; set; }
        public string? signature { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public string? file_path { get; set; }
    }
}
