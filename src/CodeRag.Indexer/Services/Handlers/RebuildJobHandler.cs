using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Git;
using CodeRag.Indexer;
using CodeRag.Indexer.Services.Indexing;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Handlers;

internal sealed class RebuildJobHandler : IIndexingJobHandler
{
    public JobType JobType => JobType.Rebuild;

    private readonly IRepositoryStore _repoStore;
    private readonly IChunkStore _chunkStore;
    private readonly ISymbolStore _symbolStore;
    private readonly IFileStore _fileStore;
    private readonly IVectorStoreFactory _vectorFactory;
    private readonly FtsHelper _fts;
    private readonly IGitService _git;
    private readonly IEmbeddingProviderFactory _embeddingsFactory;
    private readonly JobProgressReporter _progress;
    private readonly IndexJobHandler _indexHandler;
    private readonly ILogger<RebuildJobHandler> _logger;

    public RebuildJobHandler(
        IRepositoryStore repoStore,
        IChunkStore chunkStore,
        ISymbolStore symbolStore,
        IFileStore fileStore,
        IVectorStoreFactory vectorFactory,
        FtsHelper fts,
        IGitService git,
        IEmbeddingProviderFactory embeddingsFactory,
        JobProgressReporter progress,
        IndexJobHandler indexHandler,
        ILogger<RebuildJobHandler> logger)
    {
        _repoStore = repoStore;
        _chunkStore = chunkStore;
        _symbolStore = symbolStore;
        _fileStore = fileStore;
        _vectorFactory = vectorFactory;
        _fts = fts;
        _git = git;
        _embeddingsFactory = embeddingsFactory;
        _progress = progress;
        _indexHandler = indexHandler;
        _logger = logger;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        _logger.LogInformation("Rebuild job {JobId} repo {Repo}", job.Id, job.RepositoryId);
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        await _progress.SetStatsAsync(job, 0.0, phase: "clearing", ct: ct).ConfigureAwait(false);
        foreach (var domain in new[] { EmbeddingDomain.Code, EmbeddingDomain.Text })
        {
            try { await _vectorFactory.Get(domain).ResetIndexAsync(_embeddingsFactory.Get(domain).Dimensions, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Rebuild: vec reset failed for {Domain} repo {Repo}", domain.ToText(), repo.Id); }
        }
        await _vectorFactory.Code.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _vectorFactory.Text.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _fts.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _chunkStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _symbolStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        await _fileStore.DeleteByRepositoryAsync(repo.Id, ct).ConfigureAwait(false);
        _logger.LogInformation("Rebuild job {JobId}: index for repo {Repo} cleared", job.Id, repo.Id);

        await _progress.SetStatsAsync(job, job.Progress, phase: "diff", ct: ct).ConfigureAwait(false);
        var head = job.ToCommit ?? await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);
        var diff = await _git.DiffAsync(repo.LocalPath.Value, null, head, ct).ConfigureAwait(false);
        await _indexHandler.ExecuteCoreAsync(job, diff, ct).ConfigureAwait(false);
        await IndexingHelper.CommitIndexedAsync(_repoStore, repo, head, ct).ConfigureAwait(false);
    }
}
