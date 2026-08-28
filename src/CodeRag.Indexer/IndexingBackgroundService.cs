using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Infrastructure.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer;

/// <summary>
/// BackgroundService: polling + Channel consumer + FS watcher debounce (indexing.md §3).
/// Polling timer per-repo sync_interval (default из Settings, переопределяется per-repo).
/// Пропускает если Status=Paused|Syncing|Indexing. JobRunner с SemaphoreSlim(maxConcurrentIndexers).
/// </summary>
public sealed class IndexingBackgroundService : BackgroundService
{
    private readonly JobRunner _runner;
    private readonly IndexingService _indexingService;
    private readonly IRepositoryStore _repoStore;
    private readonly IndexingJobStore _jobStore;
    private readonly IGitService _git;
    private readonly ILogger<IndexingBackgroundService> _logger;
    private readonly IndexerOptions _options;

    public IndexingBackgroundService(
        JobRunner runner,
        IndexingService indexingService,
        IRepositoryStore repoStore,
        IndexingJobStore jobStore,
        IGitService git,
        IOptions<IndexerOptions> options,
        ILogger<IndexingBackgroundService> logger)
    {
        _runner = runner;
        _indexingService = indexingService;
        _repoStore = repoStore;
        _jobStore = jobStore;
        _git = git;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Indexer background service starting (maxConcurrent={Max}, syncInterval={Interval}s, watcher={Watcher})",
            _options.MaxConcurrentIndexers, _options.DefaultSyncIntervalSeconds, _options.EnableFileWatcher);

        // Восстановление queued джоб после рестарта (ADR-008)
        try { await _runner.RestoreQueuedJobsAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore queued jobs"); }

        // Запустить polling и потребление канала параллельно
        var consumeTask = ConsumeJobsAsync(stoppingToken);
        var pollTask = PollLoopAsync(stoppingToken);

        await Task.WhenAny(consumeTask, pollTask).ConfigureAwait(false);
    }

    private async Task ConsumeJobsAsync(CancellationToken ct)
    {
        await foreach (var job in _runner.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await _runner.Semaphore.WaitAsync(ct).ConfigureAwait(false);

            // Запустить джобу без блокировки канала
            _ = Task.Run(async () =>
            {
                var token = _runner.GetToken(job.Id);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                try
                {
                    await _indexingService.ExecuteAsync(job, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* already handled in service */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Job {JobId} execution error", job.Id);
                }
                finally
                {
                    _runner.RemoveCts(job.Id);
                    _runner.Semaphore.Release();
                }
            }, ct);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Polling per-repo: каждые DefaultSyncIntervalSeconds проверяем repos
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling iteration failed");
            }

            // Интервал polling — минимальный sync_interval, но проверяем per-repo внутри
            var delay = TimeSpan.FromSeconds(_options.DefaultSyncIntervalSeconds);
            if (delay < TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var repos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();

            if (!repo.Enabled) continue;
            if (repo.Status is RepositoryStatus.Paused or RepositoryStatus.Syncing or RepositoryStatus.Indexing or RepositoryStatus.Deleting) continue;

            var interval = repo.SyncIntervalSeconds ?? _options.DefaultSyncIntervalSeconds;
            var lastSync = repo.LastSyncAt ?? DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - lastSync < TimeSpan.FromSeconds(interval)) continue;

            // Trigger Sync job (который затем сделает diff и Index)
            var job = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Sync, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, null);
            await _runner.EnqueueAsync(job, ct).ConfigureAwait(false);
            _logger.LogDebug("Polling enqueued Sync job {JobId} for repo {Repo}", job.Id, repo.Name);
        }
    }
}
