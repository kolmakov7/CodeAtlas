namespace CodeRag.Indexer;

/// <summary>
/// Настройки индексации (indexing.md §3-4, ADR-008, git-proxy-auth.md §2).
/// </summary>
public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    /// <summary>Максимум конкурентных джоб индексации (SemaphoreSlim). Default 2 (Settings, indexing.md §4).</summary>
    public int MaxConcurrentIndexers { get; set; } = 2;

    /// <summary>Дефолтный интервал синхронизации одного репозитория (секунды). Per-repo переопределение через sync_interval_seconds.</summary>
    public int DefaultSyncIntervalSeconds { get; set; } = 300;

    /// <summary>Debounce для FS watcher (ms). 1–2s по indexing.md §3.</summary>
    public int FileWatcherDebounceMs { get; set; } = 1500;

    /// <summary>Включить FS watcher (только для локальной разработки; polling — источник истины).</summary>
    public bool EnableFileWatcher { get; set; } = false;

    /// <summary>Glob-игноры для watcher: .git, bin, obj, node_modules, *.tmp etc.</summary>
    public string[] WatcherIgnorePatterns { get; set; } = new[] { "*.tmp", "~*", ".git", "bin", "obj", "node_modules" };

    /// <summary>Per-repo прокси для эмбеддингов (settings embedding_proxy_id, git-proxy-auth.md §2).</summary>
    public string? DefaultEmbeddingProxyId { get; set; }

    /// <summary>Порог полной переиндексации при diff > 50% файлов (indexing.md §2, branch switch).</summary>
    public double FullReindexThreshold { get; set; } = 0.5;
}
