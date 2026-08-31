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

    // ---- Chunk budget (RAG-tuned fixed size, capped by the model context) ----

    /// <summary>
    /// Target max chunk size in characters for RAG retrieval. Oversized symbol chunks (a whole
    /// class/method) are split into windows of at most this many chars so a single embedding stays
    /// focused instead of being diluted by an entire file. 1500 chars (~375 tokens) keeps a chunk
    /// coherent yet small enough to rank well against a focused natural-language query.
    /// </summary>
    public int MaxChunkChars { get; set; } = 1500;

    /// <summary>Overlap between consecutive chunk windows as a FRACTION of the window size (for sliding-window splits).</summary>
    public double ChunkOverlapFraction { get; set; } = 0.15;

    /// <summary>Approx chars-per-token used to convert the model token context into a char cap so a chunk never overflows the context window.</summary>
    public double CharsPerToken { get; set; } = 4.0;

    /// <summary>
    /// Minimum chars for a chunk to be embedded. Chunks below this are stored for FTS/structural
    /// but skipped for embedding (avoids near-empty / noise vectors).
    /// </summary>
    public int MinEmbeddingChars { get; set; } = 32;

    /// <summary>
    /// File extensions treated as binary: skipped by the indexer entirely (not parsed, not embedded).
    /// Prevents NUL-byte / binary content from producing garbage chunks and vectors.
    /// </summary>
    public string[] BinaryExtensions { get; set; } =
    {
        "png", "jpg", "jpeg", "gif", "bmp", "ico", "tif", "tiff", "webp", "avif", "heic",
        "mp3", "wav", "ogg", "flac", "m4a", "aac", "mid",
        "mp4", "mov", "avi", "mkv", "wmv", "flv", "webm",
        "zip", "tar", "gz", "tgz", "bz2", "xz", "7z", "rar", "iso",
        "exe", "dll", "so", "dylib", "bin", "o", "obj", "a", "lib", "pdb",
        "apk", "ipa", "dmg", "msi",
        "woff", "woff2", "ttf", "otf", "eot",
        "parquet", "avro", "pkl", "pickle", "npy", "npz",
        "onnx", "gguf", "safetensors", "pt", "pth", "joblib",
        "db", "sqlite", "sqlite3", "mdb",
        "xlsx", "xls", "docx", "doc", "pptx", "pdf"
    };
}
