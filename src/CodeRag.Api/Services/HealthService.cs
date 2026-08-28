using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Indexer;
using Microsoft.Extensions.Logging;
using CodeRag.Api.Models;

namespace CodeRag.Api.Services;

public sealed class HealthService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingProvider _embedding;
    private readonly JobRunner _runner;
    private readonly ILogger<HealthService> _logger;

    public HealthService(SqliteConnectionFactory factory, IVectorStore vectorStore, IEmbeddingProvider embedding, JobRunner runner, ILogger<HealthService> logger)
    {
        _factory = factory;
        _vectorStore = vectorStore;
        _embedding = embedding;
        _runner = runner;
        _logger = logger;
    }

    public async Task<HealthDetailsDto> CheckDetailedAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();

        var db = await CheckDbAsync(ct).ConfigureAwait(false);
        var vec = await CheckVectorAsync(ct).ConfigureAwait(false);
        var emb = await CheckEmbeddingAsync(ct).ConfigureAwait(false);
        var indexer = CheckIndexer();
        var storage = CheckStorage();

        // embedding not blocking ready
        var overall = (db.Status == "Healthy" && vec.Status == "Healthy" && indexer.Status == "Healthy" && storage.Status == "Healthy")
            ? "Healthy" : "Degraded";

        if (emb.Status != "Healthy") warnings.Add("embeddings unavailable");

        return new HealthDetailsDto(overall, db, vec, emb, indexer, storage, warnings);
    }

    public async Task<(bool Healthy, string? Message)> CheckReadyAsync(CancellationToken ct = default)
    {
        // ready = DB + Vector + Indexer; embedding not blocking
        var db = await CheckDbAsync(ct).ConfigureAwait(false);
        if (db.Status != "Healthy") return (false, $"database: {db.Message}");

        var vec = await CheckVectorAsync(ct).ConfigureAwait(false);
        if (vec.Status != "Healthy") return (false, $"vector: {vec.Message}");

        var indexer = CheckIndexer();
        if (indexer.Status != "Healthy") return (false, $"indexer: {indexer.Message}");

        var storage = CheckStorage();
        if (storage.Status != "Healthy") return (false, $"storage: {storage.Message}");

        return (true, null);
    }

    private async Task<HealthComponentDto> CheckDbAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return new HealthComponentDto("Healthy", null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Health DB check failed");
            return new HealthComponentDto("Unhealthy", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<HealthComponentDto> CheckVectorAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var dummy = new float[384];
            await _vectorStore.SearchAsync(dummy, 1, null, ct).ConfigureAwait(false);
            sw.Stop();
            return new HealthComponentDto("Healthy", null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Health Vector check failed");
            // vector store with InMemory fallback should still be healthy; but if fails -> degraded
            return new HealthComponentDto("Degraded", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<HealthComponentDto> CheckEmbeddingAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Try embed single token with short timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var vecs = await _embedding.EmbedAsync(new[] { "health check" }, cts.Token).ConfigureAwait(false);
            sw.Stop();
            if (vecs.Length == 0) return new HealthComponentDto("Degraded", "empty embedding response", sw.Elapsed.TotalMilliseconds);
            return new HealthComponentDto("Healthy", null, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new HealthComponentDto("Degraded", "embedding timeout", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Embedding health check failed (not blocking)");
            return new HealthComponentDto("Degraded", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private HealthComponentDto CheckIndexer()
    {
        try
        {
            // JobRunner is healthy if channel not completed
            // If runner is null -> degraded
            if (_runner is null) return new HealthComponentDto("Degraded", "JobRunner not initialized", null);
            return new HealthComponentDto("Healthy", null, null);
        }
        catch (Exception ex)
        {
            return new HealthComponentDto("Unhealthy", ex.Message, null);
        }
    }

    private HealthComponentDto CheckStorage()
    {
        try
        {
            var dbPath = _factory.DbPath;
            var dir = Path.GetDirectoryName(dbPath) ?? "/data";
            if (!Directory.Exists(dir)) return new HealthComponentDto("Unhealthy", $"data dir missing: {dir}", null);
            var di = new DriveInfo(dir);
            // Check writable
            var testFile = Path.Combine(dir, ".healthcheck");
            try { File.WriteAllText(testFile, "ok"); File.Delete(testFile); }
            catch (Exception ex) { return new HealthComponentDto("Degraded", $"storage not writable: {ex.Message}", null); }

            return new HealthComponentDto("Healthy", null, null);
        }
        catch (Exception ex)
        {
            return new HealthComponentDto("Unhealthy", ex.Message, null);
        }
    }
}
