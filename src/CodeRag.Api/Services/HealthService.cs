using CodeRag.Api.Models;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Indexer;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Logging;

namespace CodeRag.Api.Services;

public sealed class HealthService
{
    private static readonly EmbeddingDomain[] Domains = { EmbeddingDomain.Code, EmbeddingDomain.Text };

    private readonly IDbHealthProbe _dbProbe;
    private readonly IStorageProbe _storageProbe;
    private readonly IVectorStoreFactory _vectorStoreFactory;
    private readonly IEmbeddingProviderFactory _embeddingFactory;
    private readonly IJobRunner _runner;
    private readonly ILogger<HealthService> _logger;

    public HealthService(IDbHealthProbe dbProbe, IStorageProbe storageProbe, IVectorStoreFactory vectorStoreFactory, IEmbeddingProviderFactory embeddingFactory, IJobRunner runner, ILogger<HealthService> logger)
    {
        _dbProbe = dbProbe;
        _storageProbe = storageProbe;
        _vectorStoreFactory = vectorStoreFactory;
        _embeddingFactory = embeddingFactory;
        _runner = runner;
        _logger = logger;
    }

    public async Task<HealthDetailsDto> CheckDetailedAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var db = await CheckDbAsync(ct).ConfigureAwait(false);
        var (vec, vectorIndex, vectorDomains) = await CheckVectorAsync(ct).ConfigureAwait(false);
        var (emb, embeddingDomains) = await CheckEmbeddingAsync(ct).ConfigureAwait(false);
        var indexer = CheckIndexer();
        var storage = CheckStorage();
        if (vectorIndex is { Stale: true })
            warnings.Add(vectorIndex.Reason ?? "vector index stale");
        var overall = (db.Status == "Healthy" && vec.Status == "Healthy" && indexer.Status == "Healthy" && storage.Status == "Healthy") ? "Healthy" : "Degraded";
        if (emb.Status != "Healthy") warnings.Add("embeddings unavailable");
        return new HealthDetailsDto(overall, db, vec, vectorDomains, emb, embeddingDomains, indexer, storage, warnings, vectorIndex);
    }

    public async Task<(bool Healthy, string? Message)> CheckReadyAsync(CancellationToken ct = default)
    {
        var db = await CheckDbAsync(ct).ConfigureAwait(false);
        if (db.Status != "Healthy") return (false, $"database: {db.Message}");
        var (vec, _, _) = await CheckVectorAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Checks every vector space (code / text) independently. The aggregate is Healthy only when all
    /// domains are healthy; the aggregate freshness mirrors the code domain (the primary space).
    /// </summary>
    private async Task<(HealthComponentDto Component, VectorFreshnessDto? Freshness, IReadOnlyList<VectorDomainHealthDto> Domains)> CheckVectorAsync(CancellationToken ct)
    {
        var domainResults = new List<VectorDomainHealthDto>();
        VectorFreshnessDto? aggregateFreshness = null;

        foreach (var domain in Domains)
        {
            var (component, freshness) = await CheckDomainVectorAsync(domain, ct).ConfigureAwait(false);
            domainResults.Add(new VectorDomainHealthDto(domain.ToText(), component.Status, component.Message, component.LatencyMs, freshness));
            if (domain == EmbeddingDomain.Code && aggregateFreshness is null)
                aggregateFreshness = freshness;
        }

        var anyHealthy = domainResults.Any(d => d.Status == "Healthy");
        var allHealthy = domainResults.All(d => d.Status == "Healthy");
        var message = allHealthy ? null : string.Join("; ", domainResults.Where(d => d.Status != "Healthy").Select(d => $"{d.Domain}: {d.Message ?? "degraded"}"));
        // No store registered at all => degraded; otherwise reflect whether at least one space is usable.
        var status = allHealthy ? "Healthy" : anyHealthy ? "Degraded" : "Degraded";

        return (new HealthComponentDto(status, message, null), aggregateFreshness, domainResults);
    }

    private async Task<(HealthComponentDto Component, VectorFreshnessDto? Freshness)> CheckDomainVectorAsync(EmbeddingDomain domain, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var provider = _embeddingFactory.Get(domain);
            var store = _vectorStoreFactory.Get(domain);
            var dim = provider.Dimensions > 0 ? provider.Dimensions : 384;
            var dummy = new float[dim];
            await store.SearchAsync(dummy, 1, null, ct).ConfigureAwait(false);
            sw.Stop();
            var freshness = await BuildFreshnessAsync(domain, ct).ConfigureAwait(false);
            if (freshness is { Stale: true })
                return (new HealthComponentDto("Degraded", freshness.Reason ?? "vector index stale", sw.Elapsed.TotalMilliseconds), freshness);
            return (new HealthComponentDto("Healthy", null, sw.Elapsed.TotalMilliseconds), freshness);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Health Vector check failed for {Domain}", domain.ToText());
            return (new HealthComponentDto("Degraded", ex.Message, sw.Elapsed.TotalMilliseconds), null);
        }
    }

    private async Task<VectorFreshnessDto> BuildFreshnessAsync(EmbeddingDomain domain, CancellationToken ct)
    {
        var provider = _embeddingFactory.Get(domain);
        var currentModel = provider.Model;
        int? currentDim = provider.Dimensions > 0 ? provider.Dimensions : null;
        var currentCtx = provider.ContextLength;
        try
        {
            var store = _vectorStoreFactory.Get(domain);
            var summary = await store.GetIndexSummaryAsync(ct).ConfigureAwait(false);
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
            _logger.LogDebug(ex, "Vector freshness check failed for {Domain}", domain.ToText());
            return VectorFreshnessDto.Fresh(Array.Empty<VectorModelInfoDto>(), currentModel, currentDim, currentCtx);
        }
    }

    /// <summary>
    /// Probes every embedding endpoint (code / text) independently. The aggregate is Healthy only when
    /// all configured domains respond.
    /// </summary>
    private async Task<(HealthComponentDto Component, IReadOnlyList<EmbeddingDomainHealthDto> Domains)> CheckEmbeddingAsync(CancellationToken ct)
    {
        var domainResults = new List<EmbeddingDomainHealthDto>();

        foreach (var domain in Domains)
        {
            var provider = _embeddingFactory.Get(domain);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (status, message) = await ProbeDomainAsync(provider, domain, ct).ConfigureAwait(false);
            sw.Stop();
            domainResults.Add(new EmbeddingDomainHealthDto(domain.ToText(), status, message, sw.Elapsed.TotalMilliseconds, provider.Model, provider.Dimensions));
        }

        // Embedding probe is non-blocking: any reachable endpoint keeps the component "Degraded", never "Unhealthy".
        var allHealthy = domainResults.All(d => d.Status == "Healthy");
        var aggMessage = allHealthy ? null : string.Join("; ", domainResults.Where(d => d.Status != "Healthy").Select(d => $"{d.Domain}: {d.Message ?? "unavailable"}"));
        var aggStatus = allHealthy ? "Healthy" : "Degraded";
        return (new HealthComponentDto(aggStatus, aggMessage, null), domainResults);
    }

    private async Task<(string Status, string? Message)> ProbeDomainAsync(IEmbeddingProvider provider, EmbeddingDomain domain, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var vecs = await provider.EmbedAsync(new[] { "health check" }, cts.Token).ConfigureAwait(false);
            if (vecs.Length == 0) return ("Degraded", "empty embedding response");
            return ("Healthy", null);
        }
        catch (OperationCanceledException)
        {
            return ("Degraded", "embedding timeout");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedding health check failed for {Domain} (not blocking)", domain.ToText());
            return ("Degraded", ex.Message);
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
