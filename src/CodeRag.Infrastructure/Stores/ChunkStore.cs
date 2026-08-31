using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class ChunkStore : CodeRag.Domain.Ports.IChunkStore, IChunkStore
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
INSERT INTO chunks(id, repository_id, file_id, symbol_id, language, start_line, end_line, content_hash, source, embedding_model, embedding_config_hash, embedded_at)
VALUES (@id,@repo,@file,@sym,@lang,@sl,@el,@hash,@src,@model,@cfg,@at)
ON CONFLICT(id) DO UPDATE SET
  repository_id=excluded.repository_id, file_id=excluded.file_id, symbol_id=excluded.symbol_id,
  language=excluded.language, start_line=excluded.start_line, end_line=excluded.end_line,
  content_hash=excluded.content_hash, source=excluded.source, embedding_model=excluded.embedding_model,
  embedding_config_hash=excluded.embedding_config_hash, embedded_at=excluded.embedded_at;";

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
            at = c.EmbeddedAt?.ToString("o")
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
    }
}
