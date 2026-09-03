using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using Dapper;
using System.Data;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Stores;

/// <summary>
/// Чанки живут в отдельной chunks-БД (chunks + chunks_fts). Метаданные main-БД
/// (files/symbols/repositories) подтягиваются отдельно — склейка в памяти.
/// </summary>
public sealed class ChunkStore : CodeRag.Domain.Ports.IChunkStore
{
    private readonly IDbConnectionFactory _factory;
    public ChunkStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(Chunk chunk, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await ExecUpsertAsync(conn, null, chunk).ConfigureAwait(false);
    }

    public async Task UpsertBatchAsync(IEnumerable<Chunk> chunks, CancellationToken ct = default)
    {
        var list = chunks.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
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

    private static async Task ExecUpsertAsync(IDbConnection conn, IDbTransaction? tx, Chunk c)
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
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<ChunkRow>("SELECT * FROM chunks WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Chunk>> GetByFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ChunkRow>("SELECT * FROM chunks WHERE file_id=@fid ORDER BY start_line;", new { fid = fileId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Chunk>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ChunkRow>("SELECT * FROM chunks WHERE repository_id=@repo ORDER BY file_id;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Chunk>> GetBySymbolAsync(Guid symbolId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ChunkRow>("SELECT * FROM chunks WHERE symbol_id=@sid;", new { sid = symbolId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE file_id=@fid;", new { fid = fileId.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChunkInfo>> GetSearchInfoByIdsAsync(IReadOnlyCollection<Guid> chunkIds, CancellationToken ct = default)
    {
        if (chunkIds.Count == 0) return Array.Empty<ChunkInfo>();
        var ids = chunkIds.Select(g => g.ToString()).ToArray();

        // 1) chunks — chunks DB
        using var chunksConn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var chunks = await QueryInAsync<ChunkInfoChunkRow>(chunksConn,
            "SELECT id AS chunk_id, repository_id, file_id, symbol_id, language AS chunk_language, start_line, end_line, source FROM chunks",
            "chunk_id", ids, ct).ConfigureAwait(false);

        if (chunks.Count == 0) return Array.Empty<ChunkInfo>();

        // 2) files / repositories / symbols — main DB (joined in memory)
        using var mainConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var fileIds = chunks.Where(c => Guid.TryParse(c.file_id, out _)).Select(c => c.file_id).Distinct().ToArray();
        var repoIds = chunks.Select(c => c.repository_id).Distinct().ToArray();
        var symbolIds = chunks.Where(c => !string.IsNullOrWhiteSpace(c.symbol_id)).Select(c => c.symbol_id).Distinct().ToArray();

        var files = fileIds.Length > 0
            ? (await QueryInAsync<FileMetaRow>(mainConn, "SELECT id, path, language FROM files", "id", fileIds, ct).ConfigureAwait(false)).ToDictionary(r => r.id)
            : new Dictionary<string, FileMetaRow>();
        var repos = repoIds.Length > 0
            ? (await QueryInAsync<RepoMetaRow>(mainConn, "SELECT id, name FROM repositories", "id", repoIds, ct).ConfigureAwait(false)).ToDictionary(r => r.id)
            : new Dictionary<string, RepoMetaRow>();
        var symbols = symbolIds.Length > 0
            ? (await QueryInAsync<SymbolMetaRow>(mainConn, "SELECT id, fqn, name AS symbol_name, kind AS symbol_kind, language AS symbol_language FROM symbols", "id", symbolIds, ct).ConfigureAwait(false)).ToDictionary(r => r.id)
            : new Dictionary<string, SymbolMetaRow>();

        var list = new List<ChunkInfo>(chunks.Count);
        foreach (var c in chunks)
        {
            if (!Guid.TryParse(c.chunk_id, out var cid)) continue;
            if (!Guid.TryParse(c.repository_id, out var rid)) continue;
            if (!Guid.TryParse(c.file_id, out var fid)) continue;
            Guid? sid = Guid.TryParse(c.symbol_id, out var sg) ? sg : null;
            files.TryGetValue(c.file_id, out var f);
            repos.TryGetValue(c.repository_id, out var r);
            SymbolMetaRow? sym = null;
            if (sid.HasValue && c.symbol_id is not null) symbols.TryGetValue(c.symbol_id, out sym);
            list.Add(new ChunkInfo(
                cid, rid, fid, sid,
                c.chunk_language, f?.language, sym?.symbol_language,
                f?.path ?? string.Empty, c.start_line, c.end_line, c.source ?? string.Empty,
                sym?.symbol_name, sym?.fqn, sym?.symbol_kind, r?.name));
        }
        return list;
    }

    public async Task<IReadOnlyCollection<Guid>> GetChunkIdsBySymbolIdsAsync(IReadOnlyCollection<Guid> symbolIds, CancellationToken ct = default)
    {
        if (symbolIds.Count == 0) return Array.Empty<Guid>();
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var ids = symbolIds.Select(g => g.ToString()).ToArray();
        var rows = (await QueryInAsync<string>(conn, "SELECT id FROM chunks", "symbol_id", ids, ct).ConfigureAwait(false)).ToList();
        var result = new List<Guid>(rows.Count);
        foreach (var r in rows) if (Guid.TryParse(r, out var g)) result.Add(g);
        return result;
    }

    private static async Task<IReadOnlyList<T>> QueryInAsync<T>(
        IDbConnection conn, string sqlPrefix, string column, string[] ids, CancellationToken ct)
    {
        var placeholders = string.Join(",", ids.Select((_, i) => $"@i{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"i{i}", ids[i]);
        var sql = sqlPrefix + $" WHERE {column} IN ({placeholders});";
        return (await conn.QueryAsync<T>(sql, p).ConfigureAwait(false)).ToList();
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

    private sealed class ChunkInfoChunkRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string file_id { get; set; } = "";
        public string? symbol_id { get; set; }
        public string? chunk_language { get; set; }
        public int? start_line { get; set; }
        public int? end_line { get; set; }
        public string? source { get; set; }
    }

    private sealed class FileMetaRow
    {
        public string id { get; set; } = "";
        public string? path { get; set; }
        public string? language { get; set; }
    }

    private sealed class RepoMetaRow
    {
        public string id { get; set; } = "";
        public string? name { get; set; }
    }

    private sealed class SymbolMetaRow
    {
        public string id { get; set; } = "";
        public string? fqn { get; set; }
        public string? symbol_name { get; set; }
        public string? symbol_kind { get; set; }
        public string? symbol_language { get; set; }
    }
}
