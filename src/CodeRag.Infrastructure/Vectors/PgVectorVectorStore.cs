using System.Data;
using System.Text.Json;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// pgvector-реализация IVectorStore. One isolated store per embedding domain:
///   code → vec_chunks_code (vector(&lt;codeDim&gt;)), text → vec_chunks_text (vector(&lt;textDim&gt;)),
///   in the separate &lt;base&gt;_vec database with an HNSW (vector_cosine_ops) index.
/// KNN: &lt;=&gt; cosine distance; metadata — vector_entries in the main database (repository_id denormalized).
/// Если расширение vector недоступно — делегирует в InMemoryVectorStore с warning (fallback).
/// </summary>
public sealed class PgVectorVectorStore : IVectorStore
{
    private readonly IDbConnectionFactory _factory;
    private readonly IVectorDimSource _dimSource;
    private readonly EmbeddingDomain _domain;
    private readonly ILogger<PgVectorVectorStore> _logger;
    private readonly InMemoryVectorStore _fallback;
    private bool _initialized;
    private volatile bool _tableStale;
    private volatile bool _staleWarned;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Vector table for this domain (isolated code / text spaces).</summary>
    private string Table => _domain == EmbeddingDomain.Text ? "vec_chunks_text" : "vec_chunks_code";

    /// <summary>Domain token written to vector_entries.domain.</summary>
    private string DomainToken => _domain.ToText();

    public PgVectorVectorStore(
        IDbConnectionFactory factory,
        IVectorDimSource dimSource,
        EmbeddingDomain domain,
        ILogger<PgVectorVectorStore> logger,
        ILogger<InMemoryVectorStore> fallbackLogger)
    {
        _factory = factory;
        _dimSource = dimSource;
        _domain = domain;
        _logger = logger;
        _fallback = new InMemoryVectorStore(fallbackLogger, logWarning: false);
    }

    private bool UseFallback => !_factory.IsVecAvailable;

    private int GetDim() => _dimSource.Dimensions;

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        var currentDim = GetDim();
        // Defer init only while the real dim is still unknown (<= 0).
        if (currentDim <= 0)
        {
            _logger.LogDebug("{Table} init deferred: dim not yet known (dim={Dim})", Table, currentDim);
            _initialized = false;
            return;
        }

        // A stale table (dim mismatch) is re-checked against the DB every call until resolved.
        if (_initialized && !_tableStale) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            if (UseFallback)
            {
                _logger.LogWarning("pgvector not available, PgVectorVectorStore ({Domain}) will fallback to InMemory for this process", _domain.ToText());
                _initialized = true;
                return;
            }

            using var conn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);

            // Extension is provisioned in the vec database (pgvector image ships it; best-effort).
            try
            {
                await conn.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create pgvector extension in {Database}", _factory.VecDbPath);
            }

            var existingDim = await GetVecTableDimAsync(conn, Table, ct).ConfigureAwait(false);
            if (existingDim.HasValue && existingDim.Value != currentDim)
            {
                if (!_staleWarned)
                {
                    _logger.LogWarning(
                        "{Table} has dim={Existing} but embedding provider returns dim={New} — vector index is STALE. " +
                        "New vectors will not be written until a full rebuild is run.", Table, existingDim.Value, currentDim);
                    _staleWarned = true;
                }
                _tableStale = true;
                _initialized = true;
                return;
            }

            _tableStale = false;
            _staleWarned = false;

            if (!existingDim.HasValue)
            {
                await conn.ExecuteAsync($@"
CREATE TABLE {Table} (
    chunk_id TEXT PRIMARY KEY,
    repository_id TEXT,
    embedding vector({currentDim})
);
CREATE INDEX IF NOT EXISTS idx_{Table}_embedding ON {Table} USING hnsw (embedding vector_cosine_ops);
CREATE INDEX IF NOT EXISTS idx_{Table}_repo ON {Table}(repository_id);").ConfigureAwait(false);
                _logger.LogInformation("Created {Table} table with dim={Dim} (HNSW cosine)", Table, currentDim);
            }

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
            // vector(N) reports character_maximum_length = "(N)".
            var raw = await conn.ExecuteScalarAsync<string?>(
                "SELECT character_maximum_length FROM information_schema.columns WHERE table_name = @t AND column_name = 'embedding';",
                new { t = table }).ConfigureAwait(false);
            var digits = new string(raw?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
            return int.TryParse(digits, out var d) ? d : null;
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
            await UpsertVectorEntriesAsync(list, ct).ConfigureAwait(false);
            return;
        }

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

                var json = JsonSerializer.Serialize(e.Embedding);
                await conn.ExecuteAsync(
                    $@"INSERT INTO {Table}(chunk_id, repository_id, embedding)
                       VALUES (@chunkId, @repoId, @emb::vector)
                       ON CONFLICT(chunk_id) DO UPDATE SET repository_id = EXCLUDED.repository_id, embedding = EXCLUDED.embedding;",
                    new { chunkId = e.ChunkId.ToString(), repoId = e.RepositoryId.ToString(), emb = json },
                    transaction: tx).ConfigureAwait(false);
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

        var ids = list.Select(g => g.ToString()).ToArray();
        var dp = new DynamicParameters();
        dp.Add("domain", DomainToken);
        for (var i = 0; i < ids.Length; i++) dp.Add($"id{i}", ids[i]);
        var inList = string.Join(",", ids.Select((_, i) => $"@id{i}"));

        if (UseFallback)
        {
            await _fallback.DeleteByChunkIdsAsync(list, ct).ConfigureAwait(false);
            using var conn2 = await _factory.CreateAsync(ct).ConfigureAwait(false);
            await conn2.ExecuteAsync($"DELETE FROM vector_entries WHERE domain=@domain AND chunk_id IN ({inList});", dp).ConfigureAwait(false);
            return;
        }

        using var vecConn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);
        using var metaConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = vecConn.BeginTransaction();
        using var metaTx = metaConn.BeginTransaction();
        try
        {
            try
            {
                var vecP = new DynamicParameters();
                for (var i = 0; i < ids.Length; i++) vecP.Add($"id{i}", ids[i]);
                await vecConn.ExecuteAsync($"DELETE FROM {Table} WHERE chunk_id IN ({inList});", vecP, transaction: tx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Table} delete-by-chunks skipped (table not present)", Table);
            }
            await metaConn.ExecuteAsync($"DELETE FROM vector_entries WHERE domain=@domain AND chunk_id IN ({inList});", dp, transaction: metaTx).ConfigureAwait(false);
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
        using var metaConn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var metaTx = metaConn.BeginTransaction();
        try
        {
            if (!UseFallback)
            {
                using var vecConn = await _factory.CreateVecAsync(ct).ConfigureAwait(false);
                using var tx = vecConn.BeginTransaction();
                try
                {
                    await vecConn.ExecuteAsync($"DELETE FROM {Table} WHERE repository_id = @rid;", new { rid }, transaction: tx).ConfigureAwait(false);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    _logger.LogDebug(ex, "{Table} delete-by-repository skipped (table not present)", Table);
                }
            }

            // repository_id is denormalized on vector_entries — no cross-DB join needed.
            await metaConn.ExecuteAsync(
                "DELETE FROM vector_entries WHERE domain=@domain AND repository_id = @rid;",
                new { domain = DomainToken, rid }, transaction: metaTx).ConfigureAwait(false);
            metaTx.Commit();
        }
        catch (Exception ex)
        {
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
            await connMeta.ExecuteAsync("DELETE FROM vector_entries WHERE domain=@domain AND dim <> @dim;", new { domain = DomainToken, dim = expectedDim }).ConfigureAwait(false);
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
        await metaConn.ExecuteAsync("DELETE FROM vector_entries WHERE domain=@domain;", new { domain = DomainToken }, transaction: metaTx).ConfigureAwait(false);
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
            "SELECT model, dim, context_length, COUNT(*) AS chunk_count FROM vector_entries WHERE domain=@domain GROUP BY model, dim, context_length ORDER BY model, dim;",
            new { domain = DomainToken }).ConfigureAwait(false)).ToList();
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

        try
        {
            string sql;
            object param;
            if (filter?.RepositoryIds is { Count: > 0 } repoIds)
            {
                var repoStrs = repoIds.Select(g => g.ToString()).ToArray();
                sql = $@"SELECT chunk_id, embedding <=> @qv::vector AS distance FROM {Table}
                         WHERE repository_id = ANY(@repos)
                         ORDER BY distance
                         LIMIT @k;";
                param = new { qv = json, repos = repoStrs, k };
                var rows = await conn.QueryAsync<VecRow>(sql, param).ConfigureAwait(false);
                return rows.Select(r => new ScoredChunk(r.ChunkId, CosineSimilarity(r.Distance), r.Distance)).ToList();
            }
            else
            {
                sql = $@"SELECT chunk_id, embedding <=> @qv::vector AS distance FROM {Table} ORDER BY distance LIMIT @k;";
                param = new { qv = json, k };
                var rows = await conn.QueryAsync<VecRow>(sql, param).ConfigureAwait(false);
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
    /// similarity score. Higher is more similar.
    /// </summary>
    private static double CosineSimilarity(double distance) => Math.Clamp(1.0 - distance, 0.0, 1.0);

    private async Task UpsertVectorEntriesAsync(List<VectorEntry> list, CancellationToken ct)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var e in list)
            {
                var now = DateTimeOffset.UtcNow.ToString("o");
                await conn.ExecuteAsync(
                    @"INSERT INTO vector_entries(chunk_id, repository_id, vector_id, model, dim, context_length, domain, updated_at)
                      VALUES (@chunkId, @repoId, @vectorId, @model, @dim, @ctx, @domain, @now)
                      ON CONFLICT(chunk_id) DO UPDATE SET repository_id=excluded.repository_id, vector_id=excluded.vector_id, model=excluded.model, dim=excluded.dim, context_length=excluded.context_length, domain=excluded.domain, updated_at=excluded.updated_at;",
                    new
                    {
                        chunkId = e.ChunkId.ToString(),
                        repoId = e.RepositoryId.ToString(),
                        vectorId = e.ChunkId.ToString(),
                        model = e.Model,
                        dim = e.Dim,
                        ctx = (object?)e.ContextLength ?? DBNull.Value,
                        domain = DomainToken,
                        now
                    }, transaction: tx).ConfigureAwait(false);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private sealed class VecRow
    {
        public string chunk_id { get; set; } = "";
        public double distance { get; set; }
        public Guid ChunkId => Guid.TryParse(chunk_id, out var g) ? g : Guid.Empty;
        public double Distance => distance;
    }
}
