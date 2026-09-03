using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Indexer;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Handlers;

internal sealed class EmbeddingJobHandler : IIndexingJobHandler
{
    public JobType JobType => JobType.Embedding;

    /// <summary>Чистый читатель (chunks → embeddings) — параллелен с Index/Relations в рамках репо.</summary>
    public bool BlocksRepo => false;

    private readonly IRepositoryStore _repoStore;
    private readonly IChunkStore _chunkStore;
    private readonly IEmbeddingProviderFactory _embeddingsFactory;
    private readonly IndexingJobStore _jobStore;
    private readonly JobProgressReporter _progress;
    private readonly EmbeddingService _embeddingService;
    private readonly ILogger<EmbeddingJobHandler> _logger;

    public EmbeddingJobHandler(
        IRepositoryStore repoStore,
        IChunkStore chunkStore,
        IEmbeddingProviderFactory embeddingsFactory,
        IndexingJobStore jobStore,
        JobProgressReporter progress,
        EmbeddingService embeddingService,
        ILogger<EmbeddingJobHandler> logger)
    {
        _repoStore = repoStore;
        _chunkStore = chunkStore;
        _embeddingsFactory = embeddingsFactory;
        _jobStore = jobStore;
        _progress = progress;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        var allChunks = await _chunkStore.GetByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        var groups = allChunks.GroupBy(c => c.EmbeddingDomain).ToList();
        var total = groups.Sum(g => g.Count(c => c.NeedsEmbedding(_embeddingsFactory.Get(g.Key).Model, _embeddingsFactory.Get(g.Key).ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)));
        var activeDomains = groups.Count(g => g.Any(c => c.NeedsEmbedding(_embeddingsFactory.Get(g.Key).Model, _embeddingsFactory.Get(g.Key).ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)));

        _logger.LogInformation("Embedding job {JobId}: {Total} chunks total, need re-embed in {Domains} domain(s)", job.Id, allChunks.Count, activeDomains);

        await _progress.SetStatsAsync(job, total == 0 ? 1.0 : 0.0, total, 0, "embedding", null, ct).ConfigureAwait(false);
        if (total == 0) return;

        const int batchSize = 64;
        var done = 0;
        foreach (var g in groups)
        {
            var provider = _embeddingsFactory.Get(g.Key);
            var toReembed = g.Where(c => c.NeedsEmbedding(provider.Model, provider.ConfigHash) && !string.IsNullOrWhiteSpace(c.Source)).ToList();
            for (int i = 0; i < toReembed.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = toReembed.Skip(i).Take(batchSize).ToList();
                await _embeddingService.EmbedAndUpsertAsync(batch, ct).ConfigureAwait(false);
                done += batch.Count;
                var p = (double)done / total;
                job.SetProgress(p, total, done, "embedding", null);
                await _jobStore.UpdateProgressAsync(job, ct).ConfigureAwait(false);
            }
        }

        await _progress.SetStatsAsync(job, 1.0, total, total, "done", null, ct).ConfigureAwait(false);
    }
}
