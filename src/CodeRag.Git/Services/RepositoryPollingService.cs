using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Git.Services;

/// <summary>
/// Polling per-repo sync_interval: BackgroundService, периодический обход репо.
/// Пропускает если Status=Paused|Syncing|Indexing. Timer per-repo логика через единый loop.
/// </summary>
public sealed class RepositoryPollingService : BackgroundService
{
    private readonly IRepositoryStore _store;
    private readonly IRepositoryService _service;
    private readonly GitOptions _opts;
    private readonly ILogger<RepositoryPollingService> _logger;

    public RepositoryPollingService(IRepositoryStore store, IRepositoryService service, IOptions<GitOptions> opts, ILogger<RepositoryPollingService> logger)
    {
        _store = store;
        _service = service;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Repository polling started, default interval {Sec}s", _opts.DefaultSyncIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Polling iteration failed");
            }

            // Sleep 10s between scans; per-repo due check inside
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var repos = await _store.ListAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        foreach (var repo in repos)
        {
            if (ct.IsCancellationRequested) break;

            if (!repo.Enabled) continue;
            if (repo.Status == RepositoryStatus.Paused || repo.Status == RepositoryStatus.Syncing || repo.Status == RepositoryStatus.Indexing || repo.Status == RepositoryStatus.Deleting)
                continue;

            var interval = repo.SyncIntervalSeconds ?? _opts.DefaultSyncIntervalSeconds;
            if (interval <= 0) continue;

            var lastSync = repo.LastSyncAt ?? repo.LastIndexAt;
            var due = lastSync is null || (now - lastSync.Value).TotalSeconds >= interval;

            if (!due) continue;

            _logger.LogInformation("Polling trigger sync for {Name} (interval {Sec}s)", repo.Name, interval);
            try
            {
                await _service.SyncAsync(repo.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling sync failed for {Name}", repo.Name);
            }
        }
    }
}
