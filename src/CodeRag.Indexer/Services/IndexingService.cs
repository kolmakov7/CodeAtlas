using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using CodeRag.Indexer;
using CodeRag.Indexer.Services.Handlers;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer;

public sealed class IndexingService
{
    private readonly IRepositoryStore _repoStore;
    private readonly IndexingJobStore _jobStore;
    private readonly Dictionary<Domain.Enums.JobType, IIndexingJobHandler> _handlers;
    private readonly ILogger<IndexingService> _logger;

    public IndexingService(
        IRepositoryStore repoStore,
        IndexingJobStore jobStore,
        IEnumerable<IIndexingJobHandler> handlers,
        ILogger<IndexingService> logger)
    {
        _repoStore = repoStore;
        _jobStore = jobStore;
        _handlers = handlers.ToDictionary(h => h.JobType);
        _logger = logger;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        _logger.LogInformation("Executing job {JobId} type={Type} repo={Repo}", job.Id, job.Type, job.RepositoryId);
        job.MarkRunning();
        await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
        try
        {
            if (!_handlers.TryGetValue(job.Type, out var handler))
                throw new NotSupportedException($"Unknown job type {job.Type}");
            await handler.ExecuteAsync(job, ct).ConfigureAwait(false);
            job.MarkCompleted();
            await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
            _logger.LogInformation("Job {JobId} completed", job.Id);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
            await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
            _logger.LogWarning("Job {JobId} cancelled", job.Id);
        }
        catch (Exception ex)
        {
            job.MarkFailed(IndexingHelper.TruncateError(ex.Message), DateTimeOffset.UtcNow);
            await _jobStore.UpsertAsync(job, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Job {JobId} failed", job.Id);
            try
            {
                var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false);
                if (repo is not null)
                {
                    repo.MarkError(IndexingHelper.TruncateError(ex.Message));
                    await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
                }
            }
            catch { }
            throw;
        }
    }
}
