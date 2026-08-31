using CodeRag.Api.Models;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Indexer;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Logging;

namespace CodeRag.Api.Services;

public sealed class HealthService
{
    private readonly IDbHealthProbe _dbProbe;
    private readonly IStorageProbe _storageProbe;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingProvider _embedding;
    private readonly IJobRunner _runner;
    private readonly ILogger<HealthService> _logger;

    public HealthService(IDbHealthProbe dbProbe, IStorageProbe storageProbe, IVectorStore vectorStore, IEmbeddingProvider embedding, IJobRunner runner, ILogger<HealthService> logger)
    {
        _dbProbe = dbProbe;
        _storageProbe = storageProbe;
        _vectorStore = vectorStore;
        _embedding = embedding;
        _runner = runner;
        _logger = logger;
    }

    public async Task<HealthDetailsDto> CheckDetailedAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var db = await CheckDbAsync(ct).ConfigureAwait(false);
        var (vec, vectorIndex) = await CheckVectorAsync(ct).ConfigureAwait(false);
        var emb = await CheckEmbeddingAsync(ct).ConfigureAwait(false);
        var indexer = CheckIndexer();
        var storage = CheckStorage();
        if (vectorIndex is { Stale: true })
            warnings.Add(vectorIndex.Reason ?? "vector index stale");
        var overall = (db.Status == "Healthy" && vec.Status == "Healthy" && indexer.Status == "Healthy" && storage.Status == "Healthy") ? "Healthy" : "Degraded";
        if (emb.Status != "Healthy") warnings.Add("embeddings unavailable");
        return new HealthDetailsDto(overall, db, vec, emb, indexer, storage, warnings, vectorIndex);
    }

    public async Task<(bool Healthy, string? Message)> CheckReadyAsync(CancellationToken ct = default)
    {
        var db = await CheckDbAsync(ct).ConfigureAwait(false);
        if (db.Status != "Healthy") return (false, $"database: {db.Message}");
        var (vec, _) = await CheckVectorAsync(ct).ConfigureAwait(false);
        if (vec.Status != "Healthy") return (false, $"vector: {vec.Message}");
        var indexer = CheckIndexer();
        if (indexer.Status != "Healthy") return (false, $"indexer: {indexer.Message}");
        var storage = CheckStorage();
        if (storage.Status != "Healthy") return (false, $"storage: {storage.Message}");
        return (true, null);
    }

    private Task<HealthComponentDto> CheckDbAsync(CancellationToken ct) => MapProbeAsync(_dbProbe.CheckAsync(ct));

    private async Task<HealthComponentDto> MapProbeAsync(Task<HealthProbeResult> task)
    {
        var r = await task.ConfigureAwait(false);
        return new HealthComponentDto(r.Status, r.Message, r.LatencyMs);
    }

    private async Task<(HealthComponentDto Component, VectorFreshnessDto? Freshness)> CheckVectorAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var dim = _embedding.Dimensions > 0 ? _embedding.Dimensions : 384;
            var dummy = new float[dim];
            await _vectorStore.SearchAsync(dummy, 1, null, ct).ConfigureAwait(false);
            sw.Stop();
            var freshness = await BuildFreshnessAsync(ct).ConfigureAwait(false);
            if (freshness is { Stale: true })
                return (new HealthComponentDto("Degraded", freshness.Reason ?? "vector index stale", sw.Elapsed.TotalMilliseconds), freshness);
            return (new HealthComponentDto("Healthy", null, sw.Elapsed.TotalMilliseconds), freshness);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Health Vector check failed");
            return (new HealthComponentDto("Degraded", ex.Message, sw.Elapsed.TotalMilliseconds), null);
        }
    }

    private async Task<VectorFreshnessDto> BuildFreshnessAsync(CancellationToken ct)
    {
        var currentModel = _embedding.Model;
        int? currentDim = _embedding.Dimensions > 0 ? _embedding.Dimensions : null;
        var currentCtx = _embedding.ContextLength;
        try
        {
            var summary = await _vectorStore.GetIndexSummaryAsync(ct).ConfigureAwait(false);
            var stored = summary.StoredModels
                .Select(m => new VectorModelInfoDto(m.Model, m.Dim, m.ContextLength, m.ChunkCount))
                .ToList();
            if (stored.Count == 0)
                return VectorFreshnessDto.Fresh(stored, currentModel, currentDim, currentCtx);

            var reasons = new List<string>();
            foreach (var m in summary.StoredModels)
            {
                if (currentDim is > 0 && m.Dim != currentDim.Value)
                    reasons.Add($"index built with {m.Dim}-dim vectors, current model '{currentModel}' produces {currentDim.Value}-dim");
                else if (!string.IsNullOrWhiteSpace(m.Model) && !string.Equals(m.Model, currentModel, StringComparison.OrdinalIgnoreCase))
                    reasons.Add($"index built with model '{m.Model}', current model is '{currentModel}'");
                else if (m.ContextLength is > 0 && currentCtx is > 0 && m.ContextLength != currentCtx.Value)
                    reasons.Add($"index built with context {m.ContextLength}, current model context is {currentCtx.Value}");
            }
            if (reasons.Count > 0)
                return new VectorFreshnessDto(true, string.Join("; ", reasons.Distinct()), stored, currentModel, currentDim, currentCtx);
            return VectorFreshnessDto.Fresh(stored, currentModel, currentDim, currentCtx);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vector freshness check failed");
            return VectorFreshnessDto.Fresh(Array.Empty<VectorModelInfoDto>(), currentModel, currentDim, currentCtx);
        }
    }

    private async Task<HealthComponentDto> CheckEmbeddingAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
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
            if (_runner is null) return new HealthComponentDto("Degraded", "JobRunner not initialized", null);
            return new HealthComponentDto("Healthy", null, null);
        }
        catch (Exception ex) { return new HealthComponentDto("Unhealthy", ex.Message, null); }
    }

    private HealthComponentDto CheckStorage()
    {
        var r = _storageProbe.Check();
        return new HealthComponentDto(r.Status, r.Message, r.LatencyMs);
    }
}
