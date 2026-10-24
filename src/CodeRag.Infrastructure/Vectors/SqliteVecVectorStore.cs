using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Реализация IVectorStore на sqlite-vec (vec0). One isolated store per embedding domain:
///   code → vec_chunks_code (FLOAT[<codeDim>]), text → vec_chunks_text (FLOAT[<textDim>]).
/// Dim берётся из IVectorDimSource (auto-detect из ответа API или из EmbeddingOptions).
/// Каждый домен использует СВОЙ векторный стол: векторы code и text никогда не смешиваются.
/// Метаданные хранятся в общей таблице vector_entries с колонкой domain = 'code' | 'text'.
/// Если vec0 недоступен — делегирует в InMemoryVectorStore с warning (fallback).
/// </summary>
public sealed class SqliteVecVectorStore : IVectorStore
{
    private readonly IDbConnectionFactory _factory;
    private readonly IVectorDimSource _dimSource;
    private readonly EmbeddingDomain _domain;
    private readonly ILogger<SqliteVecVectorStore> _logger;
    private readonly InMemoryVectorStore _fallback;
    private readonly string _tableName;
    private readonly string _ownerType;
    private bool _initialized;
    private int? _tableDim;
    private volatile bool _tableStale;
    private volatile bool _staleWarned;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Vector table for this domain (isolated code / text spaces).</summary>
    private string Table => _tableName;

    /// <summary>Domain token written to vector_entries.domain.</summary>
    private string DomainToken => _domain.ToText();

    public SqliteVecVectorStore(
        IDbConnectionFactory factory,
        IVectorDimSource dimSource,
        EmbeddingDomain domain,
        ILogger<SqliteVecVectorStore> logger,
        ILogger<InMemoryVectorStore> fallbackLogger,
        string? tableName = null,
        string ownerType = "chunk")
    {
        _factory = factory;
        _dimSource = dimSource;
        _domain = domain;
        _logger = logger;
        _fallback = new InMemoryVectorStore(fallbackLogger, logWarning: false);
        _tableName = string.IsNullOrWhiteSpace(tableName)
            ? (domain == EmbeddingDomain.Text ? "vec_chunks_text" : "vec_chunks_code")
            : tableName;
        _ownerType = string.IsNullOrWhiteSpace(ownerType) ? "chunk" : ownerType;
    }

    private bool UseFallback => !_factory.IsVecAvailable;

    private int GetDim() => _dimSource.Dimensions;

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        var currentDim = GetDim();
        // Defer init only while the real dim is still unknown (<= 0). A domain may legitimately be
        // 1024 (e.g. the text model), so we must NOT treat a specific dim value as "not configured".
        if (currentDim <= 0)
        {
            _logger.LogDebug("{Table} init deferred: dim not yet known (dim={Dim})", Table, currentDim);
            _initialized = false;
            return;
        }

        // A stale table (dim mismatch) is re-checked against the DB every call until resolved:
        // an explicit rebuild drops it, after which the table is recreated with the new dim.
        if (_initialized && !_tableStale) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            if (UseFallback)
            {
                _logger.LogWarning("sqlite-vec not available, SqliteVecVectorStore ({Domain}) will fallback to InMemory for this process", _domain.ToText());
                _initialized = true;
                return;
            }

            var dim = currentDim;
            if (dim <= 0)
            {
                _logger.LogWarning("Embedding dim for {Domain} is {Dim} (not yet detected), {Table} will be created on first use", _domain.ToText(), dim, Table);
                _initialized = true;
                return;
            }

            // vec0 tables live in the separate vector database.
            using var conn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);

            // Check if the table exists with matching dim.
            // A mismatch makes the index STALE: we do NOT drop it silently — the warning is surfaced
            // via diagnostics and the table is cleared only by an explicit (user-confirmed) rebuild.
            var existingDim = await GetVecTableDimAsync(conn, Table, ct).ConfigureAwait(false);
            if (existingDim.HasValue && existingDim.Value != dim)
            {
                if (!_staleWarned)
                {
                    _logger.LogWarning(
                        "{Table} has dim={Existing} but embedding provider returns dim={New} — vector index is STALE. " +
                        "New vectors will not be written until a full rebuild is run.", Table, existingDim.Value, dim);
                    _staleWarned = true;
                }
                _tableDim = existingDim.Value;
                _tableStale = true;
                _initialized = true;
                return;
            }

            _tableStale = false;
            _staleWarned = false;

            if (!existingDim.HasValue)
            {
                try
                {
                    var ddl = $@"CREATE VIRTUAL TABLE {Table} USING vec0(
    chunk_id TEXT PRIMARY KEY,
    embedding FLOAT[{dim}] distance_metric=cosine,
    repository_id TEXT
);";
                    await conn.ExecuteAsync(ddl).ConfigureAwait(false);
                    _logger.LogInformation("Created {Table} virtual table with dim={Dim}", Table, dim);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create {Table} PK variant, trying fallback DDL", Table);
                    try
                    {
                        await conn.ExecuteAsync($@"CREATE VIRTUAL TABLE {Table} USING vec0(
    embedding FLOAT[{dim}],
    chunk_id TEXT,
    repository_id TEXT
);").ConfigureAwait(false);
                        _logger.LogInformation("Created {Table} virtual table (fallback variant) with dim={Dim}", Table, dim);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Failed to create {Table} virtual table, falling back to InMemory", Table);
                    }
                }
            }

            _tableDim = dim;
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task<int?> GetVecTableDimAsync(IDbConnection conn, string table, CancellationToken ct)
    {
        try
        {
            // vec0 stores schema in sqlite_master; parse CREATE TABLE for FLOAT[<dim>].
            var sql = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT sql FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table });
            if (string.IsNullOrEmpty(sql)) return null;
            // Parse FLOAT[<dim>] from DDL
            var match = System.Text.RegularExpressions.Regex.Match(sql, @"FLOAT\[(\d+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var d)) return d;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task UpsertAsync(IEnumerable<VectorEntry> entries, CancellationToken ct = default)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (_tableStale)
        {
            // Stale table (dim mismatch): skip writes until an explicit rebuild clears the index.
            if (!_staleWarned)
            {
                _logger.LogWarning("Vector upsert skipped ({Count} entries, {Domain}): {Table} is stale (dim mismatch). Run a full rebuild to clear and re-embed.", list.Count, _domain.ToText(), Table);
                _staleWarned = true;
            }
            return;
        }

        if (UseFallback)
        {
            await _fallback.UpsertAsync(list, ct).ConfigureAwait(false);
            // Also persist metadata to vector_entries for integrity checks (even in fallback)
            await UpsertVectorEntriesAsync(list, ct).ConfigureAwait(false);
            return;
        }

        // vec0 rows go to the separate vector DB; vector_entries metadata stays in the main DB.
        // Cross-file atomicity is not guaranteed (acceptable: integrity checks reconcile orphans).
        using var conn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();

        try
        {
            foreach (var e in list)
            {
                var expectedDim = GetDim();
                if (e.Embedding.Length != expectedDim)
                {
                    _logger.LogWarning("Vector dim mismatch for chunk {ChunkId} ({Domain}): expected {Dim}, got {Got}", e.ChunkId, _domain.ToText(), expectedDim, e.Embedding.Length);
                }

                // Delete existing if any (vec0 has no native upsert, do delete+insert)
                await conn.ExecuteAsync(
                    $"DELETE FROM {Table} WHERE chunk_id = @chunkId;",
                    new { chunkId = e.ChunkId.ToString() },
                    transaction: tx);

                // Insert embedding: try JSON string via vec_f32 helper if available
                var json = JsonSerializer.Serialize(e.Embedding);
                var inserted = false;

                // Attempt 1: INSERT with vec_f32 function (common sqlite-vec)
                try
                {
                    await conn.ExecuteAsync(
                        $"INSERT INTO {Table}(chunk_id, embedding, repository_id) VALUES (@chunkId, vec_f32(@emb), @repoId);",
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
                            $"INSERT INTO {Table}(chunk_id, embedding, repository_id) VALUES (@chunkId, @emb, @repoId);",
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
                        $"INSERT INTO {Table}(chunk_id, embedding, repository_id) VALUES (@chunkId, @emb, @repoId);",
                        new { chunkId = e.ChunkId.ToString(), emb = blob, repoId = e.RepositoryId.ToString() },
                        transaction: tx);
                }
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "Failed to upsert vectors ({Domain}), rolling back", _domain.ToText());
            throw;
        }

        // Metadata (main DB), best-effort: a failure leaves orphan vec rows that integrity checks reconcile.
        await UpsertVectorEntriesAsync(list, ct).ConfigureAwait(false);
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
            var dp = new DynamicParameters();
            dp.Add("domain", DomainToken);
            dp.Add("ownerType", _ownerType);
            for (var i = 0; i < ids.Length; i++) dp.Add($"id{i}", ids[i]);
            await conn2.ExecuteAsync($"DELETE FROM vector_entries WHERE domain=@domain AND owner_type=@ownerType AND chunk_id IN ({string.Join(",", ids.Select((_, i) => $"@id{i}"))});", dp);
            return;
        }

        // vec0 rows (separate vec DB) + metadata (main DB) — two transactions.
        using var vecConn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);
        using var metaConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = vecConn.BeginTransaction();
        using var metaTx = metaConn.BeginTransaction();
        try
        {
            foreach (var id in list)
            {
                var sid = id.ToString();
                await vecConn.ExecuteAsync($"DELETE FROM {Table} WHERE chunk_id = @id;", new { id = sid }, transaction: tx);
                await metaConn.ExecuteAsync("DELETE FROM vector_entries WHERE domain=@domain AND owner_type=@ownerType AND chunk_id = @id;", new { domain = DomainToken, ownerType = _ownerType, id = sid }, transaction: metaTx);
            }
            tx.Commit();
            metaTx.Commit();
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            try { metaTx.Rollback(); } catch { }
            _logger.LogError(ex, "Failed to delete vectors ({Domain})", _domain.ToText());
            throw;
        }
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        if (UseFallback)
            await _fallback.DeleteByRepositoryAsync(repositoryId, ct).ConfigureAwait(false);

        var rid = repositoryId.ToString();
        using var vecConn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);
        using var metaConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = vecConn.BeginTransaction();
        using var metaTx = metaConn.BeginTransaction();
        try
        {
            // vec0 virtual table has no FK cascade — delete explicitly (no-op if table missing)
            try
            {
                await vecConn.ExecuteAsync($"DELETE FROM {Table} WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Table} delete-by-repository skipped (table not present)", Table);
            }
            // repository_id is denormalized on vector_entries — no cross-DB join needed.
            await metaConn.ExecuteAsync(
                @"DELETE FROM vector_entries WHERE domain=@domain AND owner_type=@ownerType AND repository_id = @rid;",
                new { domain = DomainToken, ownerType = _ownerType, rid }, transaction: metaTx).ConfigureAwait(false);
            tx.Commit();
            metaTx.Commit();
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            try { metaTx.Rollback(); } catch { }
            _logger.LogError(ex, "Failed to delete vectors for repository {Repo} ({Domain})", repositoryId, _domain.ToText());
            throw;
        }
    }

    public async Task<bool> ResetIndexAsync(int expectedDim, CancellationToken ct = default)
    {
        if (UseFallback)
        {
            var hadStale = _fallback.HasDimensionMismatch(expectedDim);
            if (hadStale) _fallback.Clear();
            using var connMeta = await _factory.CreateAsync(ct).ConfigureAwait(false);
            await connMeta.ExecuteAsync("DELETE FROM vector_entries WHERE domain=@domain AND owner_type=@ownerType AND dim <> @dim;", new { domain = DomainToken, ownerType = _ownerType, dim = expectedDim }).ConfigureAwait(false);
            if (hadStale)
                _logger.LogInformation("InMemory fallback vectors and stale metadata cleared for {Domain} (expected dim={Dim})", _domain.ToText(), expectedDim);
            return hadStale;
        }

        using var vecConn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);
        var tableDim = await GetVecTableDimAsync(vecConn, Table, ct).ConfigureAwait(false);
        if (tableDim is null || tableDim.Value == expectedDim)
            return false;

        using var metaConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = vecConn.BeginTransaction();
        using var metaTx = metaConn.BeginTransaction();
        await vecConn.ExecuteAsync($"DROP TABLE {Table};", transaction: tx).ConfigureAwait(false);
        await metaConn.ExecuteAsync("DELETE FROM vector_entries WHERE domain=@domain AND owner_type=@ownerType;", new { domain = DomainToken, ownerType = _ownerType }, transaction: metaTx).ConfigureAwait(false);
        tx.Commit();
        metaTx.Commit();
        _tableStale = false;
        _staleWarned = false;
        _initialized = false;
        _logger.LogInformation("Dropped stale {Table} (dim={Old}, expected {New}) and cleared {Domain} vector_entries — index will be rebuilt", Table, tableDim.Value, expectedDim, _domain.ToText());
        return true;
    }

    public async Task<VectorIndexSummary> GetIndexSummaryAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<IndexSummaryRow>(
            "SELECT model, dim, context_length, COUNT(*) AS chunk_count FROM vector_entries WHERE domain=@domain AND owner_type=@ownerType GROUP BY model, dim, context_length ORDER BY model, dim;",
            new { domain = DomainToken, ownerType = _ownerType }).ConfigureAwait(false)).ToList();
        return new VectorIndexSummary(rows.Select(r =>
            new VectorIndexModelInfo(r.model, r.dim, r.context_length, r.chunk_count)).ToList());
    }

    private sealed class IndexSummaryRow
    {
        public string model { get; set; } = "";
        public int dim { get; set; }
        public int? context_length { get; set; }
        public int chunk_count { get; set; }
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int k, VectorSearchFilter? filter, CancellationToken ct = default)
    {
        if (queryVector is null || queryVector.Length == 0 || k <= 0)
            return Array.Empty<ScoredChunk>();

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        if (_tableStale)
        {
            _logger.LogDebug("Vector search skipped: {Table} is stale (dim mismatch)", Table);
            return Array.Empty<ScoredChunk>();
        }

        if (UseFallback)
        {
            // As per spec: fallback returns empty OR brute-force via InMemory.
            // We implement brute-force via fallback if it has data, else empty.
            if (_fallback.Count == 0)
            {
                _logger.LogDebug("Fallback vector search returned empty (no vectors in memory, {Domain})", _domain.ToText());
                return Array.Empty<ScoredChunk>();
            }
            return await _fallback.SearchAsync(queryVector, k, filter, ct).ConfigureAwait(false);
        }

        // KNN runs against the separate vector database.
        using var conn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(queryVector);

        // Build query with optional filter
        // sqlite-vec KNN syntax: SELECT chunk_id, distance FROM {table} WHERE embedding MATCH @qv AND k=@k ORDER BY distance
        string sql;
        object param;

        try
        {
            if (filter?.RepositoryIds is { Count: > 0 } repoIds)
            {
                var repoStrs = repoIds.Select(g => g.ToString()).ToArray();
                int fetchK = Math.Max(k * 3, k + 10);
                // Correct sqlite-vec syntax: use k = ? without LIMIT
                sql = $"SELECT chunk_id, distance FROM {Table} WHERE embedding MATCH vec_f32(@qv) AND k=@k ORDER BY distance;";
                param = new { qv = json, k = fetchK };
                var rows = await TryQueryAsync(conn, sql, param, ct);
                if (rows is null)
                {
                    sql = $"SELECT chunk_id, distance FROM {Table} WHERE embedding MATCH @qv AND k=@k ORDER BY distance;";
                    param = new { qv = json, k = fetchK };
                    rows = await TryQueryAsync(conn, sql, param, ct);
                }
                if (rows is null) return Array.Empty<ScoredChunk>();

                var repoSet = new HashSet<string>(repoStrs);
                // Try to query with repository_id aux column for direct filtering
                sql = $"SELECT chunk_id, repository_id, distance FROM {Table} WHERE embedding MATCH @qv AND k=@k ORDER BY distance;";
                try
                {
                    var rows2 = await conn.QueryAsync<VecRowWithRepo>(sql, new { qv = json, k = fetchK });
                    var filtered2 = rows2.Where(r => repoSet.Contains(r.repository_id)).Take(k)
                        .Select(r => new ScoredChunk(Guid.Parse(r.chunk_id), CosineSimilarity(r.distance), r.distance))
                        .ToList();
                    return filtered2;
                }
                catch
                {
                    // chunks live in the chunks DB — query there.
                    using var chunksConn = await _factory.CreateChunksAsync(ct).ConfigureAwait(false);
                    var chunkRepoMap = await LoadChunkRepoMapAsync(chunksConn, rows.Select(r => r.ChunkId).ToList(), ct);
                    var filtered2 = rows.Where(r => chunkRepoMap.TryGetValue(r.ChunkId, out var repo) && repoSet.Contains(repo.ToString()))
                        .Take(k)
                        .Select(r => new ScoredChunk(r.ChunkId, CosineSimilarity(r.Distance), r.Distance))
                        .ToList();
                    return filtered2;
                }
            }
            else
            {
                sql = $"SELECT chunk_id, distance FROM {Table} WHERE embedding MATCH vec_f32(@qv) AND k=@k ORDER BY distance;";
                param = new { qv = json, k };
                var rows = await TryQueryAsync(conn, sql, param, ct);
                if (rows is null)
                {
                    sql = $"SELECT chunk_id, distance FROM {Table} WHERE embedding MATCH @qv AND k=@k ORDER BY distance;";
                    param = new { qv = json, k };
                    rows = await TryQueryAsync(conn, sql, param, ct);
                }
                if (rows is null)
                {
                    // Fallback with LIMIT (no k)
                    sql = $"SELECT chunk_id, distance FROM {Table} WHERE embedding MATCH @qv ORDER BY distance LIMIT @limit;";
                    param = new { qv = json, limit = k };
                    rows = await TryQueryAsync(conn, sql, param, ct);
                }
                if (rows is null) return Array.Empty<ScoredChunk>();
                return rows.Select(r => new ScoredChunk(r.ChunkId, CosineSimilarity(r.Distance), r.Distance)).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vector search failed ({Domain}), returning empty", _domain.ToText());
            return Array.Empty<ScoredChunk>();
        }
    }

    /// <summary>
    /// Convert the store's cosine <paramref name="distance"/> (= 1 − cosine) into a 0..1 cosine
    /// similarity score. Higher is more similar. Clamped because cosine distance can exceed 1
    /// (negative similarity) for dissimilar vectors.
    /// </summary>
    private static double CosineSimilarity(double distance) => Math.Clamp(1.0 - distance, 0.0, 1.0);

    private async Task<IReadOnlyList<VecRow>?> TryQueryAsync(IDbConnection conn, string sql, object param, CancellationToken ct)
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

    private async Task<Dictionary<Guid, Guid>> LoadChunkRepoMapAsync(IDbConnection conn, List<Guid> chunkIds, CancellationToken ct)
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
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        // One transaction for the whole batch: previously each row was an autocommit (one WAL
        // commit per chunk), which dominated embedding throughput (≈10s per 64-chunk batch).
        using var tx = conn.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var e in list)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO vector_entries(chunk_id, repository_id, vector_id, model, dim, context_length, domain, owner_type, updated_at)
                      VALUES (@chunkId, @repoId, @vectorId, @model, @dim, @ctx, @domain, @ownerType, @now)
                      ON CONFLICT(chunk_id) DO UPDATE SET repository_id=excluded.repository_id, vector_id=excluded.vector_id, model=excluded.model, dim=excluded.dim, context_length=excluded.context_length, domain=excluded.domain, owner_type=excluded.owner_type, updated_at=excluded.updated_at;",
                    new
                    {
                        chunkId = e.ChunkId.ToString(),
                        repoId = e.RepositoryId.ToString(),
                        vectorId = e.ChunkId.ToString(),
                        model = e.Model,
                        dim = e.Dim,
                        ctx = (object?)e.ContextLength ?? DBNull.Value,
                        domain = DomainToken,
                        ownerType = _ownerType,
                        now
                    },
                    transaction: tx);
            }
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { }
            throw;
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
