using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git;
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
    private readonly ILogger<IndexingBackgroundService> _logger;
    private readonly IOptionsMonitor<IndexerOptions> _optionsMonitor;
    private IndexerOptions Opts => _optionsMonitor.CurrentValue;

    public IndexingBackgroundService(
        JobRunner runner,
        IndexingService indexingService,
        IRepositoryStore repoStore,
        IndexingJobStore jobStore,
        IOptionsMonitor<IndexerOptions> options,
        ILogger<IndexingBackgroundService> logger)
    {
        _runner = runner;
        _indexingService = indexingService;
        _repoStore = repoStore;
        _jobStore = jobStore;
        _optionsMonitor = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Indexer background service starting (maxConcurrent={Max}, syncInterval={Interval}s, watcher={Watcher})",
            Opts.MaxConcurrentIndexers, Opts.DefaultSyncIntervalSeconds, Opts.EnableFileWatcher);

        // Восстановление queued джоб после рестарта (ADR-008)
        try { await _runner.RestoreQueuedJobsAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore queued jobs"); }

        // Репозитории, застрявшие в Syncing/Indexing после рестарта, разблокируем (джобы их уже в очереди)
        try
        {
            var repos = await _repoStore.ListAsync(stoppingToken).ConfigureAwait(false);
            foreach (var r in repos.Where(r => r.Status is RepositoryStatus.Syncing or RepositoryStatus.Indexing))
            {
                var stuck = r.Status;
                r.MarkActive();
                await _repoStore.UpsertAsync(r, stoppingToken).ConfigureAwait(false);
                _logger.LogWarning("Reset stuck repo {Repo} status {Status} -> Active", r.Name, stuck);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to reset stuck repositories"); }

        // Запустить polling и потребление канала параллельно
        var consumeTask = ConsumeJobsAsync(stoppingToken);
        var pollTask = PollLoopAsync(stoppingToken);

        await Task.WhenAny(consumeTask, pollTask).ConfigureAwait(false);
    }

    private async Task ConsumeJobsAsync(CancellationToken ct)
    {
        await foreach (var job in _runner.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // Токен джобы берём ДО ожидания слота: отменённая джоба не должна
            // сидеть в ожидании свободного воркера до последнего
            var token = _runner.GetToken(job.Id);

            // Если джоба в БД больше не в очереди (например, отменена через API), пропускаем
            try
            {
                var fresh = await _jobStore.GetByIdAsync(job.Id, ct).ConfigureAwait(false);
                if (fresh is not null && fresh.Status != JobStatus.Queued)
                {
                    _logger.LogInformation("Skipping job {JobId}: status is {Status}", job.Id, fresh.Status);
                    // CTS удаляем только если джоба точно не выполняется (Running — выполняется, CTS уберёт finally)
                    if (fresh.Status is JobStatus.Cancelled or JobStatus.Completed or JobStatus.Failed)
                        _runner.RemoveCts(job.Id);
                    continue;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* БД временно недоступна — выполняем как обычно */ }

            try
            {
                await _runner.Semaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Job {JobId} was cancelled while waiting for a worker slot", job.Id);
                _runner.RemoveCts(job.Id);
                continue;
            }

            // Запустить джобу без блокировки канала. Per-repo слот берётся внутри, чтобы
            // не заслонять канал: джобы других репозиториев запускаются параллельно.
            // Слот нужен только джобам, мутирующим git (clone/fetch — ref-lock race);
            // Index/Embedding/Relations/Rebuild — читатели и идут параллельно в рамках репо.
            var blocksRepo = _indexingService.IsRepoBlocking(job.Type);
            _ = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                try
                {
                    if (blocksRepo)
                        await _runner.EnterRepoAsync(job.RepositoryId, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Отменена, пока ждала per-repo слота (ExecuteAsync не запускался)
                    try
                    {
                        job.MarkCancelled();
                        await _jobStore.UpsertAsync(job, CancellationToken.None).ConfigureAwait(false);
                        _logger.LogWarning("Job {JobId} cancelled while waiting for repo slot", job.Id);
                    }
                    catch { }
                    _runner.RemoveCts(job.Id);
                    _runner.Semaphore.Release();
                    return;
                }

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
                    if (blocksRepo)
                        _runner.ExitRepo(job.RepositoryId);
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
            var delay = TimeSpan.FromSeconds(Opts.DefaultSyncIntervalSeconds);
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

            var interval = repo.SyncIntervalSeconds ?? Opts.DefaultSyncIntervalSeconds;
            var lastSync = repo.LastSyncAt ?? DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - lastSync < TimeSpan.FromSeconds(interval)) continue;

            // Trigger Sync job (который затем сделает diff и Index)
            var job = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Sync, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, null);
            await _runner.EnqueueAsync(job, ct).ConfigureAwait(false);
            _logger.LogDebug("Polling enqueued Sync job {JobId} for repo {Repo}", job.Id, repo.Name);
        }
    }
}
