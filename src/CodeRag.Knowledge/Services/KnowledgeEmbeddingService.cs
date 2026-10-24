using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Embeddings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Knowledge.Services;

public sealed class KnowledgeEmbeddingService
{
    private readonly IEmbeddingProviderFactory _providers;
    private readonly IVectorStoreFactory _vectors;
    private readonly IKnowledgeStore _knowledge;
    private readonly IOptionsMonitor<KnowledgeOptions> _options;
    private readonly ILogger<KnowledgeEmbeddingService> _logger;

    public KnowledgeEmbeddingService(
        IEmbeddingProviderFactory providers,
        IVectorStoreFactory vectors,
        IKnowledgeStore knowledge,
        IOptionsMonitor<KnowledgeOptions> options,
        ILogger<KnowledgeEmbeddingService> logger)
    {
        _providers = providers;
        _vectors = vectors;
        _knowledge = knowledge;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> TryEmbedAsync(Guid knowledgeId, CancellationToken ct = default)
    {
        try
        {
            var entry = await _knowledge.GetByIdAsync(knowledgeId, ct).ConfigureAwait(false);
            if (entry is null) return false;
            var provider = _providers.Text;
            var text = BuildText(entry.Title, entry.Content, _options.CurrentValue.Embeddings.MaxChars);
            var vectors = await provider.EmbedAsync(new[] { text }, ct).ConfigureAwait(false);
            if (vectors.Length == 0) return false;

            var vector = vectors[0];
            await _vectors.Knowledge.UpsertAsync(new[]
            {
                new VectorEntry(entry.Id, entry.RepositoryId ?? Guid.Empty, vector,
                    provider.Model, vector.Length, provider.ContextLength, "knowledge")
            }, ct).ConfigureAwait(false);
            await _knowledge.MarkEmbeddedAsync(entry.Id, provider.Model, provider.ConfigHash,
                DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Knowledge embedding failed for {KnowledgeId}; entry stays pending", knowledgeId);
            return false;
        }
    }

    public async Task<int> EmbedPendingAsync(int limit = 100, CancellationToken ct = default)
    {
        var provider = _providers.Text;
        var candidates = await _knowledge.GetEmbeddingCandidatesAsync(
            provider.Model, provider.ConfigHash, limit, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        var batchSize = Math.Clamp(_options.CurrentValue.Embeddings.BatchSize, 1, 256);
        var maxChars = _options.CurrentValue.Embeddings.MaxChars;
        var embedded = 0;
        foreach (var batch in candidates.Chunk(batchSize))
        {
            try
            {
                var texts = batch.Select(e => BuildText(e.Title, e.Content, maxChars)).ToArray();
                var vectors = await provider.EmbedAsync(texts, ct).ConfigureAwait(false);
                if (vectors.Length != batch.Length)
                {
                    _logger.LogWarning("Embedding count mismatch for knowledge: {Got}/{Expected}", vectors.Length, batch.Length);
                    continue;
                }
                var entries = batch.Select((e, i) => new VectorEntry(
                    e.Id, e.RepositoryId ?? Guid.Empty, vectors[i], provider.Model, vectors[i].Length,
                    provider.ContextLength, "knowledge")).ToList();
                await _vectors.Knowledge.UpsertAsync(entries, ct).ConfigureAwait(false);
                foreach (var entry in batch)
                {
                    await _knowledge.MarkEmbeddedAsync(entry.Id, provider.Model, provider.ConfigHash,
                        DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                    embedded++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch knowledge embedding failed for {Count} entries", batch.Length);
            }
        }
        return embedded;
    }

    public async Task DeleteAsync(Guid knowledgeId, CancellationToken ct = default)
    {
        try
        {
            await _vectors.Knowledge.DeleteByChunkIdsAsync(new[] { knowledgeId }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete knowledge vector {KnowledgeId}", knowledgeId);
        }
    }

    private static string BuildText(string title, string content, int maxChars)
    {
        var limit = Math.Clamp(maxChars, 200, 100_000);
        var text = title + "\n\n" + content;
        return text.Length <= limit ? text : text[..limit];
    }
}
