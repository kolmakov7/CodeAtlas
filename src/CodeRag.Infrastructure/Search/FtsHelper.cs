using CodeRag.Infrastructure.Persistence;
using Dapper;

namespace CodeRag.Infrastructure.Search;

/// <summary>
/// Helper для FTS5 (chunks_fts). Ручной sync вместо триггеров (триггеры опционально).
/// Виртуальная таблица: chunks_fts(chunk_id UNINDEXED, repository_id UNINDEXED, content, symbol_name, fqn, file_path, namespace, signature, tokenize='trigram')
/// </summary>
public sealed class FtsHelper
{
    private readonly SqliteConnectionFactory _factory;

    public FtsHelper(SqliteConnectionFactory factory) => _factory = factory;

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
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await UpsertAsync(conn, chunkId, repositoryId, content, symbolName, fqn, filePath, ns, signature, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
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
        var cid = chunkId.ToString();
        var rid = repositoryId.ToString();

        // FTS5 upsert = delete + insert (no native upsert)
        await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid }).ConfigureAwait(false);

        await conn.ExecuteAsync(
            @"INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path, namespace, signature)
              VALUES (@cid, @rid, @content, @sym, @fqn, @fp, @ns, @sig);",
            new
            {
                cid,
                rid,
                content = content ?? "",
                sym = symbolName ?? "",
                fqn = fqn ?? "",
                fp = filePath ?? "",
                ns = ns ?? "",
                sig = signature ?? ""
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Батч upsert: один connection + одна транзакция на все строки (вместо
    /// отдельного соединения на чанк). DELETE всех chunk_id, затем INSERT всех.
    /// </summary>
    public async Task UpsertBatchAsync(IEnumerable<FtsUpsertRow> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var r in list)
                await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid = r.ChunkId.ToString() }, tx).ConfigureAwait(false);
            foreach (var r in list)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path, namespace, signature)
                      VALUES (@cid, @rid, @content, @sym, @fqn, @fp, @ns, @sig);",
                    new
                    {
                        cid = r.ChunkId.ToString(),
                        rid = r.RepositoryId.ToString(),
                        content = r.Content ?? "",
                        sym = r.SymbolName ?? "",
                        fqn = r.Fqn ?? "",
                        fp = r.FilePath ?? "",
                        ns = r.Namespace ?? "",
                        sig = r.Signature ?? ""
                    }, tx).ConfigureAwait(false);
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
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE chunk_id = @cid;", new { cid = chunkId.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByChunkIdsAsync(IEnumerable<Guid> chunkIds, CancellationToken ct = default)
    {
        var list = chunkIds.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
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
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM chunks_fts WHERE repository_id = @rid;", new { rid = repositoryId.ToString() }).ConfigureAwait(false);
    }

    /// <summary>
    /// Поиск по FTS5. Возвращает список chunk_id с ранком.
    /// query — текст в синтаксисе FTS5 (например, токен или &quot;phrase&quot;).
    /// </summary>
    public async Task<IReadOnlyList<FtsResult>> SearchAsync(string query, int limit = 20, Guid? repositoryId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<FtsResult>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        // FTS5 MATCH syntax: chunks_fts MATCH @q
        // Optional repository filter via auxiliary column repository_id (UNINDEXED) - filter via WHERE clause
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
            return rows.Select(r => new FtsResult(Guid.Parse(r.chunk_id), Guid.TryParse(r.repository_id, out var g) ? g : Guid.Empty, r.rank)).ToList();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // Если запрос содержит спецсимволы несовместимые с trigram, fallback: экранировать
            // Попробуем простой LIKE-like escape: обернуть в двойные кавычки
            try
            {
                var escaped = $"\"{query.Replace("\"", "\"\"")}\"";
                object param2 = repositoryId.HasValue
                    ? new { q = escaped, rid = repositoryId.Value.ToString(), limit }
                    : new { q = escaped, limit };
                var rows2 = await conn.QueryAsync<FtsRow>(sql, param2).ConfigureAwait(false);
                return rows2.Select(r => new FtsResult(Guid.Parse(r.chunk_id), Guid.TryParse(r.repository_id, out var g) ? g : Guid.Empty, r.rank)).ToList();
            }
            catch
            {
                throw new InvalidOperationException($"FTS query failed for '{query}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Batch upsert для чанков с подтяжкой метаданных из chunks/symbols/files.
    /// Удобно при переиндексации: берет content и символы из БД.
    /// </summary>
    public async Task SyncChunkAsync(Guid chunkId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SyncRow>(
            @"SELECT c.id as chunk_id, c.repository_id, c.source as content, s.name as symbol_name, s.fqn, f.path as file_path, s.namespace, s.signature
              FROM chunks c
              LEFT JOIN symbols s ON s.id = c.symbol_id
              LEFT JOIN files f ON f.id = c.file_id
              WHERE c.id = @id;",
            new { id = chunkId.ToString() }).ConfigureAwait(false);

        if (row is null) return;
        await UpsertAsync(conn,
            Guid.Parse(row.chunk_id),
            Guid.Parse(row.repository_id),
            row.content ?? "",
            row.symbol_name,
            row.fqn,
            row.file_path,
            row.@namespace,
            row.signature,
            ct).ConfigureAwait(false);
    }

    private sealed class FtsRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public double rank { get; set; }
    }

    private sealed class SyncRow
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string? content { get; set; }
        public string? symbol_name { get; set; }
        public string? fqn { get; set; }
        public string? file_path { get; set; }
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
