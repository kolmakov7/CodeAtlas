using CodeRag.Git.Services;
using Microsoft.Extensions.Logging;

namespace CodeRag.Git.Watching;

/// <summary>
/// FS watcher опционально (FileSystemWatcher debounce 1s). Только для локальной разработки.
/// Триггерит SyncAsync раньше таймера. Игнорирует .git/, bin/, obj/, node_modules/, *.tmp, ~*.
/// Массовые изменения (checkout ветки) — coalesce в один sync.
/// </summary>
public sealed class RepositoryFileWatcher : IDisposable
{
    private readonly Guid _repositoryId;
    private readonly string _localPath;
    private readonly IRepositoryService _service;
    private readonly ILogger _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();
    private bool _pending;
    private bool _disposed;

    public RepositoryFileWatcher(Guid repositoryId, string localPath, IRepositoryService service, ILogger logger)
    {
        _repositoryId = repositoryId;
        _localPath = localPath;
        _service = service;
        _logger = logger;
    }

    public void Start()
    {
        if (!Directory.Exists(_localPath))
        {
            _logger.LogWarning("Watcher not started, path not exists {Path}", _localPath);
            return;
        }

        _watcher = new FileSystemWatcher(_localPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;

        _debounceTimer = new Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);

        _logger.LogInformation("File watcher started for {Path}", _localPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;

        lock (_lock)
        {
            _pending = true;
            _debounceTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounce(object? _)
    {
        bool shouldRun;
        lock (_lock)
        {
            shouldRun = _pending;
            _pending = false;
        }
        if (!shouldRun) return;

        _logger.LogInformation("Watcher debounce trigger sync for {Id}", _repositoryId);
        _ = Task.Run(async () =>
        {
            try { await _service.SyncAsync(_repositoryId).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Watcher sync failed for {Id}", _repositoryId); }
        });
    }

    private static bool ShouldIgnore(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (name.StartsWith("~") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;

        // Ignore patterns: .git, bin, obj, node_modules
        var lower = fullPath.ToLowerInvariant();
        if (lower.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") || lower.EndsWith($"{Path.DirectorySeparatorChar}.git")) return true;
        if (lower.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) return true;
        if (lower.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) return true;
        if (lower.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")) return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer?.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}

/// <summary>Фабрика вотчеров (DI).</summary>
public sealed class RepositoryFileWatcherFactory
{
    private readonly IRepositoryService _service;
    private readonly ILogger<RepositoryFileWatcherFactory> _logger;

    public RepositoryFileWatcherFactory(IRepositoryService service, ILogger<RepositoryFileWatcherFactory> logger)
    {
        _service = service;
        _logger = logger;
    }

    public RepositoryFileWatcher Create(Guid repositoryId, string localPath)
        => new(repositoryId, localPath, _service, _logger);
}
