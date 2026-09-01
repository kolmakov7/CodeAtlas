namespace CodeRag.Git;

/// <summary>Опции для Git CLI обёртки.</summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>Путь к исполняемому git (по умолчанию "git").</summary>
    public string GitExecutable { get; set; } = "git";

    /// <summary>Таймаут по умолчанию для git команд (сек). Для больших репо clone через SOCKS может занять &gt;120s.</summary>
    public int DefaultTimeoutSeconds { get; set; } = 600;

    /// <summary>Корень для локальных репо — должен быть /data/repos.</summary>
    public string ReposRoot { get; set; } = "/data/repos";

    /// <summary>Директория для SSH ключей (/data/keys/ssh).</summary>
    public string SshKeysDir { get; set; } = "/data/keys/ssh";

    /// <summary>Включает FS watcher (только для локальной разработки).</summary>
    public bool EnableFileWatcher { get; set; } = false;

    /// <summary>Дефолтный sync interval сек (переопределяется per-repo).</summary>
    public int DefaultSyncIntervalSeconds { get; set; } = 300;

    /// <summary>NO_PROXY список (через запятую) — прокидывается в env.</summary>
    public string? NoProxy { get; set; }
}
