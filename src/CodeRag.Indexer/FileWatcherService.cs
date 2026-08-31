using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Infrastructure.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer;

/// <summary>
/// FS watcher с debounce 1–2s, игнорирует *.tmp, ~*, .git/, bin/, obj/, node_modules (indexing.md §3).
/// Не источник истины — лишь триггер fetch раньше таймера. Массовые изменения coalesce в один sync.
/// Опционален, только для локальной разработки (EnableFileWatcher).
/// </summary>
public sealed class FileWatcherService : BackgroundService
{
    private readonly IRepositoryStore _repoStore;
    private readonly JobRunner _runner;
    private readonly ILogger<FileWatcherService> _logger;
    private readonly IOptionsMonitor<IndexerOptions> _optionsMonitor;
    private IndexerOptions Opts => _optionsMonitor.CurrentValue;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<Guid, DateTime> _lastTrigger = new();
    private readonly Dictionary<Guid, Timer?> _debounceTimers = new();
    private readonly object _lock = new();

    public FileWatcherService(
        IRepositoryStore repoStore,
        JobRunner runner,
        IOptionsMonitor<IndexerOptions> options,
        ILogger<FileWatcherService> logger)
    {
        _repoStore = repoStore;
        _runner = runner;
        _optionsMonitor = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Opts.EnableFileWatcher)
        {
            _logger.LogInformation("File watcher disabled (EnableFileWatcher=false) — polling is source of truth");
            return;
        }

        // Инициализировать watchers для существующих репо
        await RefreshWatchersAsync(stoppingToken).ConfigureAwait(false);

        // Периодически обновлять список репо (новые/удалённые)
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { await RefreshWatchersAsync(stoppingToken).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Refresh watchers failed"); }
        }
    }

    private async Task RefreshWatchersAsync(CancellationToken ct)
    {
        var repos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
        var repoIds = repos.Select(r => r.Id).ToHashSet();

        lock (_lock)
        {
            // Удалить watchers для удалённых репо
            for (int i = _watchers.Count - 1; i >= 0; i--)
            {
                // watcher.Tag хранит repoId в виде строки в пути
                // Упростим: пересоздаём все при изменении списка
            }
        }

        // Пересоздаём watchers если список изменился
        var currentPaths = _watchers.Select(w => w.Path).ToHashSet(StringComparer.Ordinal);
        var desiredPaths = repos.Where(r => Directory.Exists(r.LocalPath.Value)).Select(r => r.LocalPath.Value).ToHashSet(StringComparer.Ordinal);

        if (currentPaths.SetEquals(desiredPaths)) return;

        DisposeWatchers();

        foreach (var repo in repos)
        {
            if (!Directory.Exists(repo.LocalPath.Value)) continue;
            try
            {
                var watcher = new FileSystemWatcher(repo.LocalPath.Value)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true
                };
                var repoId = repo.Id;
                watcher.Changed += (_, e) => OnChanged(repoId, e.FullPath);
                watcher.Created += (_, e) => OnChanged(repoId, e.FullPath);
                watcher.Deleted += (_, e) => OnChanged(repoId, e.FullPath);
                watcher.Renamed += (_, e) => OnChanged(repoId, e.FullPath);
                lock (_lock) _watchers.Add(watcher);
                _logger.LogInformation("Watching {Path} for repo {Repo}", repo.LocalPath.Value, repo.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to watch {Path}", repo.LocalPath.Value);
            }
        }
    }

    private void OnChanged(Guid repoId, string fullPath)
    {
        if (ShouldIgnore(fullPath)) return;

        lock (_lock)
        {
            // Debounce: сбрасываем таймер на каждый чейндж, coalesce массовые изменения
            if (_debounceTimers.TryGetValue(repoId, out var existing) && existing is not null)
            {
                try { existing.Dispose(); } catch { }
            }

            var timer = new Timer(async _ =>
            {
                try
                {
                    var repo = await _repoStore.GetByIdAsync(repoId).ConfigureAwait(false);
                    if (repo is null || repo.Status is RepositoryStatus.Paused or RepositoryStatus.Deleting || !repo.Enabled) return;

                    var job = IndexingJob.Create(Guid.NewGuid(), repoId, JobType.Sync, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, null);
                    await _runner.EnqueueAsync(job).ConfigureAwait(false);
                    _logger.LogInformation("FS watcher triggered Sync job {JobId} for repo {Repo} (debounced)", job.Id, repo.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FS watcher trigger failed for repo {Repo}", repoId);
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_debounceTimers.TryGetValue(repoId, out var t)) { try { t?.Dispose(); } catch { } _debounceTimers.Remove(repoId); }
                    }
                }
            }, null, Opts.FileWatcherDebounceMs, Timeout.Infinite);

            _debounceTimers[repoId] = timer;
        }
    }

    private bool ShouldIgnore(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("~")) return true;
        if (fullPath.Contains("/.git/", StringComparison.Ordinal) || fullPath.Contains("\\.git\\", StringComparison.Ordinal)) return true;
        if (fullPath.Contains("/bin/", StringComparison.Ordinal) || fullPath.Contains("\\bin\\", StringComparison.Ordinal)) return true;
        if (fullPath.Contains("/obj/", StringComparison.Ordinal) || fullPath.Contains("\\obj\\", StringComparison.Ordinal)) return true;
        if (fullPath.Contains("/node_modules/", StringComparison.Ordinal) || fullPath.Contains("\\node_modules\\", StringComparison.Ordinal)) return true;
        foreach (var pat in Opts.WatcherIgnorePatterns)
        {
            if (pat.StartsWith("*") && name.EndsWith(pat[1..], StringComparison.OrdinalIgnoreCase)) return true;
            if (fullPath.Contains(pat, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void DisposeWatchers()
    {
        lock (_lock)
        {
            foreach (var w in _watchers) try { w.Dispose(); } catch { }
            _watchers.Clear();
            foreach (var kv in _debounceTimers) try { kv.Value?.Dispose(); } catch { }
            _debounceTimers.Clear();
        }
    }

    public override void Dispose()
    {
        DisposeWatchers();
        base.Dispose();
    }
}
