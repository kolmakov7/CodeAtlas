using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class ChunkStore : CodeRag.Domain.Ports.IChunkStore
{
    private readonly IDbConnectionFactory _factory;
    public ChunkStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Chunk chunk, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await ExecUpsertAsync(conn, null, chunk).ConfigureAwait(false);
    }

    public async Task UpsertBatchAsync(IEnumerable<Chunk> chunks, CancellationToken ct = default)
    {
        var list = chunks.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var c in list) await ExecUpsertAsync(conn, tx, c).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task ExecUpsertAsync(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx, Chunk c)
    {
        const string sql = @"
INSERT INTO chunks(id, repository_id, file_id, symbol_id, language, start_line, end_line, content_hash, source, embedding_model, embedding_config_hash, embedded_at, embedding_domain)
VALUES (@id,@repo,@file,@sym,@lang,@sl,@el,@hash,@src,@model,@cfg,@at,@domain)
ON CONFLICT(id) DO UPDATE SET
  repository_id=excluded.repository_id, file_id=excluded.file_id, symbol_id=excluded.symbol_id,
  language=excluded.language, start_line=excluded.start_line, end_line=excluded.end_line,
  content_hash=excluded.content_hash, source=excluded.source, embedding_model=excluded.embedding_model,
  embedding_config_hash=excluded.embedding_config_hash, embedded_at=excluded.embedded_at,
  embedding_domain=excluded.embedding_domain;";

        await conn.ExecuteAsync(sql, new
        {
            id = c.Id.ToString(),
            repo = c.RepositoryId.ToString(),
            file = c.FileId.ToString(),
            sym = c.SymbolId?.ToString(),
            lang = c.Language,
            sl = c.StartLine,
            el = c.EndLine,
            hash = c.ContentHash,
            src = c.Source,
            model = c.EmbeddingModel,
            cfg = c.EmbeddingConfigHash,
            at = c.EmbeddedAt?.ToString("o"),
            domain = c.EmbeddingDomain.ToText()
        }, transaction: tx).ConfigureAwait(false);
    }

    public async Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<ChunkRow>("SELECT * FROM chunks WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Chunk>> GetByFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ChunkRow>("SELECT * FROM chunks WHERE file_id=@fid ORDER BY start_line;", new { fid = fileId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Chunk>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ChunkRow>("SELECT * FROM chunks WHERE repository_id=@repo ORDER BY file_id;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Chunk>> GetBySymbolAsync(Guid symbolId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ChunkRow>("SELECT * FROM chunks WHERE symbol_id=@sid;", new { sid = symbolId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE file_id=@fid;", new { fid = fileId.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChunkInfo>> GetSearchInfoByIdsAsync(IReadOnlyCollection<Guid> chunkIds, CancellationToken ct = default)
    {
        if (chunkIds.Count == 0) return Array.Empty<ChunkInfo>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ids = chunkIds.Select(g => g.ToString()).ToArray();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@c{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"c{i}", ids[i]);
        var sql = $@"SELECT c.id as chunk_id, c.repository_id, c.file_id, c.symbol_id, c.language as chunk_language, c.start_line, c.end_line, c.source,
        f.path as file_path, f.language as file_language, r.name as repository_name, s.fqn, s.name as symbol_name, s.kind as symbol_kind, s.language as symbol_language
 FROM chunks c JOIN files f ON f.id = c.file_id LEFT JOIN repositories r ON r.id = c.repository_id LEFT JOIN symbols s ON s.id = c.symbol_id WHERE c.id IN ({placeholders});";
        var rows = (await conn.QueryAsync<ChunkInfoRow>(sql, p).ConfigureAwait(false)).ToList();
        var list = new List<ChunkInfo>(rows.Count);
        foreach (var r in rows)
        {
            if (!Guid.TryParse(r.chunk_id, out var cid)) continue;
            if (!Guid.TryParse(r.repository_id, out var rid)) continue;
            if (!Guid.TryParse(r.file_id, out var fid)) continue;
            Guid? sid = Guid.TryParse(r.symbol_id, out var sg) ? sg : null;
            list.Add(new ChunkInfo(cid, rid, fid, sid, r.chunk_language, r.file_language, r.symbol_language, r.file_path ?? string.Empty, r.start_line, r.end_line, r.source ?? string.Empty, r.symbol_name, r.fqn, r.symbol_kind, r.repository_name));
        }
        return list;
    }

    public async Task<IReadOnlyCollection<Guid>> GetChunkIdsBySymbolIdsAsync(IReadOnlyCollection<Guid> symbolIds, CancellationToken ct = default)
    {
        if (symbolIds.Count == 0) return Array.Empty<Guid>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ids = symbolIds.Select(g => g.ToString()).ToArray();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@s{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"s{i}", ids[i]);
        var sql = $"SELECT id FROM chunks WHERE symbol_id IN ({placeholders});";
        var rows = (await conn.QueryAsync<string>(sql, p).ConfigureAwait(false)).ToList();
        var result = new List<Guid>(rows.Count);
        foreach (var r in rows) if (Guid.TryParse(r, out var g)) result.Add(g);
        return result;
    }

    private static Chunk Map(ChunkRow r)
    {
        return Chunk.Rehydrate(
            Guid.Parse(r.id),
            Guid.Parse(r.repository_id),
            Guid.Parse(r.file_id),
            r.source,
            r.content_hash,
            string.IsNullOrWhiteSpace(r.symbol_id) ? null : Guid.Parse(r.symbol_id),
            r.language,
            r.start_line,
            r.end_line,
            r.embedding_model,
            r.embedding_config_hash,
            r.embedding_domain,
            string.IsNullOrWhiteSpace(r.embedded_at) ? null : DateTimeOffset.Parse(r.embedded_at));
    }

    private sealed class ChunkRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string file_id { get; set; } = "";
        public string? symbol_id { get; set; }
        public string? language { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public string content_hash { get; set; } = "";
        public string source { get; set; } = "";
        public string? embedding_model { get; set; }
        public string? embedding_config_hash { get; set; }
        public string? embedded_at { get; set; }
        public string? embedding_domain { get; set; }
    }

    private sealed class ChunkInfoRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string file_id { get; set; } = "";
        public string? symbol_id { get; set; }
        public string? chunk_language { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public string? source { get; set; }
        public string? file_path { get; set; }
        public string? file_language { get; set; }
        public string? fqn { get; set; }
        public string? symbol_name { get; set; }
        public string? symbol_kind { get; set; }
        public string? symbol_language { get; set; }
        public string? repository_name { get; set; }
    }
}
