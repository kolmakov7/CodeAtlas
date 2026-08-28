using System.Threading.Channels;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer;

/// <summary>
/// Реализация IJobRunner на Channel + SemaphoreSlim + persistence + CTS per job + Progress (indexing.md §4, ADR-008).
/// </summary>
public sealed class JobRunner : IJobRunner, IAsyncDisposable
{
    private readonly Channel<IndexingJob> _channel;
    private readonly SemaphoreSlim _semaphore;
    private readonly IndexingJobStore _store;
    private readonly ILogger<JobRunner> _logger;
    private readonly IndexerOptions _options;

    private readonly Dictionary<Guid, CancellationTokenSource> _ctsMap = new();
    private readonly object _ctsLock = new();

    public JobRunner(IndexingJobStore store, IOptions<IndexerOptions> options, ILogger<JobRunner> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
        var max = _options.MaxConcurrentIndexers <= 0 ? 2 : _options.MaxConcurrentIndexers;
        _semaphore = new SemaphoreSlim(max, max);
        _channel = Channel.CreateUnbounded<IndexingJob>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Channel для консумера (BackgroundService).</summary>
    public ChannelReader<IndexingJob> Reader => _channel.Reader;

    /// <summary>Семафор для ограничения конкурентности.</summary>
    public SemaphoreSlim Semaphore => _semaphore;

    public async Task<Guid> EnqueueAsync(IndexingJob job, CancellationToken ct = default)
    {
        if (job is null) throw new ArgumentNullException(nameof(job));

        // Persistence: вставка как Queued
        await _store.UpsertAsync(job, ct).ConfigureAwait(false);

        // In-memory очередь
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);

        // CTS per job для отмены
        var cts = new CancellationTokenSource();
        lock (_ctsLock) _ctsMap[job.Id] = cts;

        _logger.LogInformation("Enqueued job {JobId} repo={Repo} type={Type} from={From} to={To}", job.Id, job.RepositoryId, job.Type, job.FromCommit, job.ToCommit);
        return job.Id;
    }

    public async Task CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        CancellationTokenSource? cts = null;
        lock (_ctsLock) _ctsMap.TryGetValue(jobId, out cts);

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            _logger.LogInformation("Cancelled job {JobId} (running)", jobId);
        }
        else
        {
            _logger.LogInformation("Cancel requested for job {JobId} (queued or not running)", jobId);
        }

        var job = await _store.GetByIdAsync(jobId, ct).ConfigureAwait(false);
        if (job is not null && job.Status is JobStatus.Queued or JobStatus.Running)
        {
            job.MarkCancelled();
            await _store.UpsertAsync(job, ct).ConfigureAwait(false);
        }
    }

    public Task<IndexingJob?> GetAsync(Guid jobId, CancellationToken ct = default) =>
        _store.GetByIdAsync(jobId, ct);

    public Task<IReadOnlyList<IndexingJob>> ListAsync(Guid? repositoryId = null, CancellationToken ct = default) =>
        _store.ListAsync(repositoryId, ct);

    /// <summary>Получить CTS для выполнения джобы (вызывается исполнителем).</summary>
    public CancellationToken GetToken(Guid jobId)
    {
        lock (_ctsLock)
        {
            if (_ctsMap.TryGetValue(jobId, out var cts)) return cts.Token;
            // Если не было Enqueue (восстановление после рестарта), создаём новый CTS
            var newCts = new CancellationTokenSource();
            _ctsMap[jobId] = newCts;
            return newCts.Token;
        }
    }

    public void RemoveCts(Guid jobId)
    {
        lock (_ctsLock)
        {
            if (_ctsMap.TryGetValue(jobId, out var cts))
            {
                try { cts.Dispose(); } catch { }
                _ctsMap.Remove(jobId);
            }
        }
    }

    /// <summary>Восстановление queued джоб после рестарта (ADR-008) — кладёт их обратно в Channel.</summary>
    public async Task RestoreQueuedJobsAsync(CancellationToken ct = default)
    {
        var queued = await _store.ListByStatusAsync(JobStatus.Queued, ct).ConfigureAwait(false);
        foreach (var j in queued)
        {
            await _channel.Writer.WriteAsync(j, ct).ConfigureAwait(false);
            lock (_ctsLock)
            {
                if (!_ctsMap.ContainsKey(j.Id))
                    _ctsMap[j.Id] = new CancellationTokenSource();
            }
            _logger.LogInformation("Restored queued job {JobId} type={Type} repo={Repo}", j.Id, j.Type, j.RepositoryId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _semaphore.Dispose();
        await Task.CompletedTask;
    }
}
