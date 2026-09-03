using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;
using System.Data;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Search;

/// <summary>
/// Helper для full-text поиска по чанкам (chunks_fts) в chunks-БД. Ручной sync вместо триггеров.
/// SQLite: FTS5 (tokenize='trigram') + bm25-подобный rank.
/// PostgreSQL: обычная таблица chunks_fts + pg_trgm GIN индексы, поиск через ILIKE
/// (rank = -число совпавших колонок, меньше = лучше, как в FTS5).
/// </summary>
public sealed class FtsHelper
{
    private readonly IDbConnectionFactory _factory;

    public FtsHelper(IDbConnectionFactory factory) => _factory = factory;

    private bool IsPostgres => SqlDialect.IsPostgres(_factory);

    public async Task UpsertAsync(
        Guid chunkId,
        Guid repositoryId,
        string content,
        string? symbolName,
        string? fqn,
        string? filePath,
        string? ns,
        string? signature,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await UpsertAsync(conn, chunkId, repositoryId, content, symbolName, fqn, filePath, ns, signature, ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="conn"/> должен быть соединением с chunks-БД.</summary>
    public async Task UpsertAsync(
        IDbConnection conn,
        Guid chunkId,
        Guid repositoryId,
        string content,
        string? symbolName,
        string? fqn,
        string? filePath,
        string? ns,
        string? signature,
        CancellationToken ct = default)
    {
        var p = Parameters(chunkId, repositoryId, content, symbolName, fqn, filePath, ns, signature);
        if (IsPostgres)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path, "namespace", signature)
                VALUES (@cid, @rid, @content, @sym, @fqn, @fp, @ns, @sig)
                ON CONFLICT(chunk_id) DO UPDATE SET
                    repository_id = EXCLUDED.repository_id,
                    content = EXCLUDED.content,
                    symbol_name = EXCLUDED.symbol_name,
                    fqn = EXCLUDED.fqn,
                    file_path = EXCLUDED.file_path,
                    "namespace" = EXCLUDED."namespace",
                    signature = EXCLUDED.signature;
                """, p, transaction: null).ConfigureAwait(false);
        }
        else
        {
            // FTS5 upsert = delete + insert (no native upsert)
            await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid = chunkId.ToString() }).ConfigureAwait(false);
            await conn.ExecuteAsync(SqliteInsertSql, p).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Батч upsert: один connection + одна транзакция на все строки (вместо
    /// отдельного соединения на чанк).
    /// </summary>
    public async Task UpsertBatchAsync(IEnumerable<FtsUpsertRow> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            if (IsPostgres)
            {
                foreach (var r in list)
                    await conn.ExecuteAsync(PostgresUpsertSql, Parameters(r), transaction: tx).ConfigureAwait(false);
            }
            else
            {
                foreach (var r in list)
                    await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid = r.ChunkId.ToString() }, transaction: tx).ConfigureAwait(false);
                foreach (var r in list)
                    await conn.ExecuteAsync(SqliteInsertSql, Parameters(r), transaction: tx).ConfigureAwait(false);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task DeleteAsync(Guid chunkId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid = chunkId.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByChunkIdsAsync(IEnumerable<Guid> chunkIds, CancellationToken ct = default)
    {
        var list = chunkIds.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var id in list)
                await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid = id.ToString() }, transaction: tx).ConfigureAwait(false);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE repository_id = @rid;", new { rid = repositoryId.ToString() }).ConfigureAwait(false);
    }

    /// <summary>
    /// Поиск по FTS. Возвращает список chunk_id с ранком (меньше = лучше).
    /// SQLite: query — текст в синтаксисе FTS5 (например, токен или &quot;phrase&quot;).
    /// PostgreSQL: подстрока (trigram ILIKE), синтаксис FTS5 не поддерживается.
    /// </summary>
    public async Task<IReadOnlyList<FtsResult>> SearchAsync(string query, int limit = 20, Guid? repositoryId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<FtsResult>();
        using var conn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);

        if (IsPostgres)
        {
            var like = $"%{query}%";
            string pgSql;
            object pgParam;
            if (repositoryId.HasValue)
            {
                pgSql = @"SELECT chunk_id, repository_id,
                        -((content ILIKE @like)::int + (symbol_name ILIKE @like)::int + (fqn ILIKE @like)::int + (file_path ILIKE @like)::int + (""namespace"" ILIKE @like)::int + (signature ILIKE @like)::int) AS rank
                        FROM chunks_fts
                        WHERE repository_id = @rid
                          AND (content ILIKE @like OR symbol_name ILIKE @like OR fqn ILIKE @like OR file_path ILIKE @like OR ""namespace"" ILIKE @like OR signature ILIKE @like)
                        ORDER BY rank, chunk_id
                        LIMIT @limit;";
                pgParam = new { like, rid = repositoryId.Value.ToString(), limit };
            }
            else
            {
                pgSql = @"SELECT chunk_id, repository_id,
                        -((content ILIKE @like)::int + (symbol_name ILIKE @like)::int + (fqn ILIKE @like)::int + (file_path ILIKE @like)::int + (""namespace"" ILIKE @like)::int + (signature ILIKE @like)::int) AS rank
                        FROM chunks_fts
                        WHERE content ILIKE @like OR symbol_name ILIKE @like OR fqn ILIKE @like OR file_path ILIKE @like OR ""namespace"" ILIKE @like OR signature ILIKE @like
                        ORDER BY rank, chunk_id
                        LIMIT @limit;";
                pgParam = new { like, limit };
            }
            var rows = await conn.QueryAsync<FtsRow>(pgSql, pgParam).ConfigureAwait(false);
            return rows.Select(MapResult).Where(r => r is not null).ToList();
        }

        string sql;
        object param;
        if (repositoryId.HasValue)
        {
            sql = @"SELECT chunk_id, repository_id, rank FROM chunks_fts WHERE chunks_fts MATCH @q AND repository_id = @rid ORDER BY rank LIMIT @limit;";
            param = new { q = query, rid = repositoryId.Value.ToString(), limit };
        }
        else
        {
            sql = @"SELECT chunk_id, repository_id, rank FROM chunks_fts WHERE chunks_fts MATCH @q ORDER BY rank LIMIT @limit;";
            param = new { q = query, limit };
        }

        try
        {
            var rows = await conn.QueryAsync<FtsRow>(sql, param).ConfigureAwait(false);
            return rows.Select(MapResult).Where(r => r is not null).ToList();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // Если запрос содержит спецсимволы несовместимые с trigram, fallback: экранировать
            try
            {
                var escaped = $"\"{query.Replace("\"", "\"\"")}\"";
                object param2 = repositoryId.HasValue
                    ? new { q = escaped, rid = repositoryId.Value.ToString(), limit }
                    : new { q = escaped, limit };
                var rows2 = await conn.QueryAsync<FtsRow>(sql, param2).ConfigureAwait(false);
                return rows2.Select(MapResult).Where(r => r is not null).ToList();
            }
            catch
            {
                throw new InvalidOperationException($"FTS query failed for '{query}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Batch upsert для чанков с подтяжкой метаданных из chunks/symbols/files.
    /// Чанк читается из chunks-БД, символы/файлы — из main-БД (склейка в памяти).
    /// </summary>
    public async Task SyncChunkAsync(Guid chunkId, CancellationToken ct = default)
    {
        var id = chunkId.ToString();
        using var chunksConn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
        var row = await chunksConn.QuerySingleOrDefaultAsync<ChunkSyncRow>(
            "SELECT id AS chunk_id, repository_id, source AS content, symbol_id, file_id FROM chunks WHERE id = @id;",
            new { id }).ConfigureAwait(false);
        if (row is null) return;

        string? symbolName = null, fqn = null, ns = null, signature = null, filePath = null;
        using var mainConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(row.symbol_id))
        {
            var sym = await mainConn.QuerySingleOrDefaultAsync<SymbolSyncRow>(
                "SELECT name AS symbol_name, fqn, \"namespace\" AS namespace, signature FROM symbols WHERE id = @sid;",
                new { sid = row.symbol_id }).ConfigureAwait(false);
            if (sym is not null)
            {
                symbolName = sym.symbol_name;
                fqn = sym.fqn;
                ns = sym.@namespace;
                signature = sym.signature;
            }
        }
        if (!string.IsNullOrWhiteSpace(row.file_id))
        {
            filePath = await mainConn.QuerySingleOrDefaultAsync<string>(
                "SELECT path FROM files WHERE id = @fid;", new { fid = row.file_id }).ConfigureAwait(false);
        }

        await UpsertAsync(chunksConn,
            Guid.Parse(row.chunk_id),
            Guid.Parse(row.repository_id),
            row.content ?? "",
            symbolName,
            fqn,
            filePath,
            ns,
            signature,
            ct).ConfigureAwait(false);
    }

    private static FtsResult? MapResult(FtsRow r)
    {
        if (!Guid.TryParse(r.chunk_id, out var cid)) return null;
        var rid = Guid.TryParse(r.repository_id, out var g) ? g : Guid.Empty;
        return new FtsResult(cid, rid, r.rank);
    }

    private static object Parameters(FtsUpsertRow r) =>
        Parameters(r.ChunkId, r.RepositoryId, r.Content, r.SymbolName, r.Fqn, r.FilePath, r.Namespace, r.Signature);

    private static object Parameters(Guid chunkId, Guid repositoryId, string content, string? symbolName, string? fqn, string? filePath, string? ns, string? signature) =>
        new
        {
            cid = chunkId.ToString(),
            rid = repositoryId.ToString(),
            content = content ?? "",
            sym = symbolName ?? "",
            fqn = fqn ?? "",
            fp = filePath ?? "",
            ns = ns ?? "",
            sig = signature ?? ""
        };

    private const string SqliteInsertSql = @"
INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path, namespace, signature)
VALUES (@cid, @rid, @content, @sym, @fqn, @fp, @ns, @sig);";

    private const string PostgresUpsertSql = @"
INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path, ""namespace"", signature)
VALUES (@cid, @rid, @content, @sym, @fqn, @fp, @ns, @sig)
ON CONFLICT(chunk_id) DO UPDATE SET
    repository_id = EXCLUDED.repository_id,
    content = EXCLUDED.content,
    symbol_name = EXCLUDED.symbol_name,
    fqn = EXCLUDED.fqn,
    file_path = EXCLUDED.file_path,
    ""namespace"" = EXCLUDED.""namespace"",
    signature = EXCLUDED.signature;";

    private sealed class FtsRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public double rank { get; set; }
    }

    private sealed class ChunkSyncRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string? content { get; set; }
        public string? symbol_id { get; set; }
        public string? file_id { get; set; }
    }

    private sealed class SymbolSyncRow
    {
        public string? symbol_name { get; set; }
        public string? fqn { get; set; }
        public string? @namespace { get; set; }
        public string? signature { get; set; }
    }
}

public sealed record FtsResult(Guid ChunkId, Guid RepositoryId, double Rank);

/// <summary>Строка для батч upsert в chunks_fts.</summary>
public sealed record FtsUpsertRow(
    Guid ChunkId,
    Guid RepositoryId,
    string Content,
    string? SymbolName,
    string? Fqn,
    string? FilePath,
    string? Namespace,
    string? Signature);
