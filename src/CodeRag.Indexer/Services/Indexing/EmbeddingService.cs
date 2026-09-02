using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Indexing;

internal sealed class EmbeddingService
{
    private readonly IChunkStore _chunkStore;
    private readonly IVectorStoreFactory _vectorFactory;
    private readonly IEmbeddingProviderFactory _embeddingsFactory;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        IChunkStore chunkStore,
        IVectorStoreFactory vectorFactory,
        IEmbeddingProviderFactory embeddingsFactory,
        ILogger<EmbeddingService> logger)
    {
        _chunkStore = chunkStore;
        _vectorFactory = vectorFactory;
        _embeddingsFactory = embeddingsFactory;
        _logger = logger;
    }

    public async Task EmbedAndUpsertAsync(List<Domain.Entities.Chunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        var domain = chunks[0].EmbeddingDomain;
        var provider = _embeddingsFactory.Get(domain);
        var store = _vectorFactory.Get(domain);
        var texts = chunks.Select(c => c.Source).ToList();

        _logger.LogInformation("Embedding {Count} {Domain} chunks model={Model} config={Cfg}", chunks.Count, domain.ToText(), provider.Model, provider.ConfigHash);

        float[][] vectors;
        try
        {
            vectors = await provider.EmbedAsync(texts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding failed for {Count} {Domain} chunks, FTS still available", chunks.Count, domain.ToText());
            return;
        }

        var entries = new List<VectorEntry>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var ch = chunks[i];
            var vec = vectors[i];
            entries.Add(new VectorEntry(ch.Id, ch.RepositoryId, vec, provider.Model, vec.Length, provider.ContextLength));
            ch.MarkEmbedded(provider.Model, provider.ConfigHash);
            await _chunkStore.UpsertAsync(ch, ct).ConfigureAwait(false);
        }

        try { await store.UpsertAsync(entries, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Vector upsert failed for {Count} {Domain} chunks", entries.Count, domain.ToText()); }
    }
}
