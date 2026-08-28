using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using CodeRag.Infrastructure.Persistence;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Реализация IVectorStore на sqlite-vec (vec0).
/// CREATE VIRTUAL TABLE vec_chunks USING vec0(embedding float[384], chunk_id TEXT, repository_id TEXT)
/// Если vec0 недоступен — делегирует в InMemoryVectorStore с warning (fallback).
/// </summary>
public sealed class SqliteVecVectorStore : IVectorStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteVecVectorStore> _logger;
    private readonly InMemoryVectorStore _fallback;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private const int Dim = 384;

    public SqliteVecVectorStore(SqliteConnectionFactory factory, ILogger<SqliteVecVectorStore> logger, ILogger<InMemoryVectorStore> fallbackLogger)
    {
        _factory = factory;
        _logger = logger;
        _fallback = new InMemoryVectorStore(fallbackLogger, logWarning: false);
    }

    private bool UseFallback => !_factory.IsVecAvailable;

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            if (UseFallback)
            {
                _logger.LogWarning("sqlite-vec not available, SqliteVecVectorStore will fallback to InMemory for this process");
                _initialized = true;
                return;
            }

            using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

            // Check if vec_chunks exists
            var exists = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='vec_chunks';");

            if (exists == 0)
            {
                try
                {
                    // Preferred DDL per ADR-002 + task: auxiliary columns repository_id
                    // Try variant with PRIMARY KEY
                    var ddl = @"CREATE VIRTUAL TABLE vec_chunks USING vec0(
    chunk_id TEXT PRIMARY KEY,
    embedding FLOAT[384] distance_metric=cosine,
    repository_id TEXT
);";
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = ddl;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("Created vec_chunks virtual table (PK variant)");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create vec_chunks PK variant, trying fallback DDL");
                    try
                    {
                        using var cmd2 = conn.CreateCommand();
                        cmd2.CommandText = @"CREATE VIRTUAL TABLE vec_chunks USING vec0(
    embedding FLOAT[384],
    chunk_id TEXT,
    repository_id TEXT
);";
                        await cmd2.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        _logger.LogInformation("Created vec_chunks virtual table (fallback variant)");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Failed to create vec_chunks virtual table, falling back to InMemory");
                        // Don't throw — fallback will handle later
                    }
                }
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (UseFallback)
        {
            await _fallback.UpsertAsync(list, ct).ConfigureAwait(false);
            // Also persist metadata to vector_entries for integrity checks (even in fallback)
            await UpsertVectorEntriesAsync(list, ct).ConfigureAwait(false);
            return;
        }

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();

        try
        {
            foreach (var e in list)
            {
                if (e.Embedding.Length != Dim)
                {
                    _logger.LogWarning("Vector dim mismatch for chunk {ChunkId}: expected {Dim}, got {Got}", e.ChunkId, Dim, e.Embedding.Length);
                }

                // Delete existing if any (vec0 has no native upsert, do delete+insert)
                await conn.ExecuteAsync(
                    "DELETE FROM vec_chunks WHERE chunk_id = @chunkId;",
                    new { chunkId = e.ChunkId.ToString() },
                    transaction: tx);

                // Insert embedding: try JSON string via vec_f32 helper if available
                var json = JsonSerializer.Serialize(e.Embedding);
                var inserted = false;

                // Attempt 1: INSERT with vec_f32 function (common sqlite-vec)
                try
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO vec_chunks(chunk_id, embedding, repository_id) VALUES (@chunkId, vec_f32(@emb), @repoId);",
                        new { chunkId = e.ChunkId.ToString(), emb = json, repoId = e.RepositoryId.ToString() },
                        transaction: tx);
                    inserted = true;
                }
                catch (Exception ex1)
                {
                    _logger.LogDebug(ex1, "vec_f32 insert failed for {ChunkId}, trying direct JSON", e.ChunkId);
                }

                if (!inserted)
                {
                    try
                    {
                        // Attempt 2: direct JSON string (vec0 may auto-parse)
                        await conn.ExecuteAsync(
                            "INSERT INTO vec_chunks(chunk_id, embedding, repository_id) VALUES (@chunkId, @emb, @repoId);",
                            new { chunkId = e.ChunkId.ToString(), emb = json, repoId = e.RepositoryId.ToString() },
                            transaction: tx);
                        inserted = true;
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "Failed to insert vector for chunk {ChunkId} via JSON, trying blob", e.ChunkId);
                    }
                }

                if (!inserted)
                {
                    // Attempt 3: blob encoding (float32 little-endian)
                    var blob = FloatArrayToBlob(e.Embedding);
                    await conn.ExecuteAsync(
                        "INSERT INTO vec_chunks(chunk_id, embedding, repository_id) VALUES (@chunkId, @emb, @repoId);",
                        new { chunkId = e.ChunkId.ToString(), emb = blob, repoId = e.RepositoryId.ToString() },
                        transaction: tx);
                }

                // Upsert metadata
                var now = DateTimeOffset.UtcNow.ToString("o");
                await conn.ExecuteAsync(
                    @"INSERT INTO vector_entries(chunk_id, vector_id, model, dim, updated_at)
                      VALUES (@chunkId, @vectorId, @model, @dim, @now)
                      ON CONFLICT(chunk_id) DO UPDATE SET vector_id=excluded.vector_id, model=excluded.model, dim=excluded.dim, updated_at=excluded.updated_at;",
                    new { chunkId = e.ChunkId.ToString(), vectorId = e.ChunkId.ToString(), model = e.Model, dim = e.Dim, now },
                    transaction: tx);
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to upsert vectors, rolling back");
            throw;
        }
    }

    public async Task DeleteByChunkIdsAsync(IEnumerable<Guid> chunkIds, CancellationToken ct = default)
    {
        var list = chunkIds.ToList();
        if (list.Count == 0) return;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (UseFallback)
        {
            await _fallback.DeleteByChunkIdsAsync(list, ct).ConfigureAwait(false);
            using var conn2 = await _factory.CreateAsync(ct).ConfigureAwait(false);
            var ids = list.Select(g => g.ToString()).ToArray();
            await conn2.ExecuteAsync($"DELETE FROM vector_entries WHERE chunk_id IN ({string.Join(",", ids.Select((_, i) => $"@id{i}"))});",
                ToDapperParams(ids));
            return;
        }

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var id in list)
            {
                var sid = id.ToString();
                await conn.ExecuteAsync("DELETE FROM vec_chunks WHERE chunk_id = @id;", new { id = sid }, transaction: tx);
                await conn.ExecuteAsync("DELETE FROM vector_entries WHERE chunk_id = @id;", new { id = sid }, transaction: tx);
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to delete vectors");
            throw;
        }
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int k, VectorSearchFilter? filter, CancellationToken ct = default)
    {
        if (queryVector is null || queryVector.Length == 0 || k <= 0)
            return Array.Empty<ScoredChunk>();

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (UseFallback)
        {
            // As per spec: fallback returns empty OR brute-force via InMemory.
            // We implement brute-force via fallback if it has data, else empty.
            if (_fallback.Count == 0)
            {
                _logger.LogDebug("Fallback vector search returned empty (no vectors in memory)");
                return Array.Empty<ScoredChunk>();
            }
            return await _fallback.SearchAsync(queryVector, k, filter, ct).ConfigureAwait(false);
        }

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(queryVector);

        // Build query with optional filter
        // sqlite-vec KNN syntax: SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH @qv AND k=@k ORDER BY distance
        // If filter by repositories, add auxiliary column filter.
        string sql;
        object param;

        // Note: vec0 auxiliary filter via repository_id not well supported for pre-filter, we do post-filter via vector_entries join or simple WHERE.
        // Try simplest: SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH @qv AND k=@k ORDER BY distance LIMIT @k

        try
        {
            if (filter?.RepositoryIds is { Count: > 0 } repoIds)
            {
                var repoStrs = repoIds.Select(g => g.ToString()).ToArray();
                int fetchK = Math.Max(k * 3, k + 10);
                // Correct sqlite-vec syntax: use k = ? without LIMIT
                sql = "SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH vec_f32(@qv) AND k=@k ORDER BY distance;";
                param = new { qv = json, k = fetchK };
                var rows = await TryQueryAsync(conn, sql, param, ct);
                if (rows is null)
                {
                    sql = "SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH @qv AND k=@k ORDER BY distance;";
                    param = new { qv = json, k = fetchK };
                    rows = await TryQueryAsync(conn, sql, param, ct);
                }
                if (rows is null) return Array.Empty<ScoredChunk>();

                var repoSet = new HashSet<string>(repoStrs);
                // Try to query with repository_id aux column for direct filtering
                sql = "SELECT chunk_id, repository_id, distance FROM vec_chunks WHERE embedding MATCH @qv AND k=@k ORDER BY distance;";
                try
                {
                    var rows2 = await conn.QueryAsync<VecRowWithRepo>(sql, new { qv = json, k = fetchK });
                    var filtered2 = rows2.Where(r => repoSet.Contains(r.repository_id)).Take(k)
                        .Select(r => new ScoredChunk(Guid.Parse(r.chunk_id), 1.0 / (1.0 + r.distance), r.distance))
                        .ToList();
                    return filtered2;
                }
                catch
                {
                    var chunkRepoMap = await LoadChunkRepoMapAsync(conn, rows.Select(r => r.ChunkId).ToList(), ct);
                    var filtered2 = rows.Where(r => chunkRepoMap.TryGetValue(r.ChunkId, out var repo) && repoSet.Contains(repo.ToString()))
                        .Take(k)
                        .Select(r => new ScoredChunk(r.ChunkId, 1.0 / (1.0 + r.Distance), r.Distance))
                        .ToList();
                    return filtered2;
                }
            }
            else
            {
                sql = "SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH vec_f32(@qv) AND k=@k ORDER BY distance;";
                param = new { qv = json, k };
                var rows = await TryQueryAsync(conn, sql, param, ct);
                if (rows is null)
                {
                    sql = "SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH @qv AND k=@k ORDER BY distance;";
                    param = new { qv = json, k };
                    rows = await TryQueryAsync(conn, sql, param, ct);
                }
                if (rows is null)
                {
                    // Fallback with LIMIT (no k)
                    sql = "SELECT chunk_id, distance FROM vec_chunks WHERE embedding MATCH @qv ORDER BY distance LIMIT @limit;";
                    param = new { qv = json, limit = k };
                    rows = await TryQueryAsync(conn, sql, param, ct);
                }
                if (rows is null) return Array.Empty<ScoredChunk>();
                return rows.Select(r => new ScoredChunk(r.ChunkId, 1.0 / (1.0 + r.Distance), r.Distance)).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector search failed, returning empty (fallback note: brute-force not implemented for sqlite-vec failure)");
            return Array.Empty<ScoredChunk>();
        }
    }

    private async Task<IReadOnlyList<VecRow>?> TryQueryAsync(SqliteConnection conn, string sql, object param, CancellationToken ct)
    {
        try
        {
            var rows = (await conn.QueryAsync<VecRow>(sql, param)).ToList();
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vec query failed: {Sql}", sql);
            return null;
        }
    }

    private async Task<Dictionary<string, string>> LoadVectorEntriesAsync(SqliteConnection conn, string[] chunkIds, CancellationToken ct)
    {
        // vector_entries doesn't have repository_id; return empty
        return new Dictionary<string, string>();
    }

    private async Task<Dictionary<Guid, Guid>> LoadChunkRepoMapAsync(SqliteConnection conn, List<Guid> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return new();
        var ids = chunkIds.Select(g => g.ToString()).ToArray();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        var sql = $"SELECT id, repository_id FROM chunks WHERE id IN ({placeholders});";
        var rows = await conn.QueryAsync<(string id, string repository_id)>(sql, ToDapperParams(ids));
        var dict = new Dictionary<Guid, Guid>();
        foreach (var r in rows)
        {
            if (Guid.TryParse(r.id, out var cid) && Guid.TryParse(r.repository_id, out var rid))
                dict[cid] = rid;
        }
        return dict;
    }

    private async Task UpsertVectorEntriesAsync(List<VectorEntry> list, CancellationToken ct)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        foreach (var e in list)
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            await conn.ExecuteAsync(
                @"INSERT INTO vector_entries(chunk_id, vector_id, model, dim, updated_at)
                  VALUES (@chunkId, @vectorId, @model, @dim, @now)
                  ON CONFLICT(chunk_id) DO UPDATE SET vector_id=excluded.vector_id, model=excluded.model, dim=excluded.dim, updated_at=excluded.updated_at;",
                new { chunkId = e.ChunkId.ToString(), vectorId = e.ChunkId.ToString(), model = e.Model, dim = e.Dim, now });
        }
    }

    private static byte[] FloatArrayToBlob(float[] arr)
    {
        var bytes = new byte[arr.Length * 4];
        Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static DynamicParameters ToDapperParams(string[] ids)
    {
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) p.Add($"id{i}", ids[i]);
        return p;
    }

    private sealed class VecRow
    {
        public string chunk_id { get; set; } = "";
        public double distance { get; set; }
        public Guid ChunkId => Guid.TryParse(chunk_id, out var g) ? g : Guid.Empty;
        public double Distance => distance;
    }

    private sealed class VecRowWithRepo
    {
        public string chunk_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public double distance { get; set; }
    }
}
