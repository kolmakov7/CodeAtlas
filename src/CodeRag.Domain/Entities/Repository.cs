using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.ValueObjects;

namespace CodeRag.Domain.Entities;

/// <summary>
/// Корень агрегата репозитория. Маппится на таблицу repositories (storage.md §2).
/// Инварианты:
/// - Name уникален, не пуст.
/// - GitUrl валиден (https/ssh/git@).
/// - LocalPath внутри /data/repos.
/// - После успешной индексации IndexedCommit == CurrentCommit.
/// </summary>
public sealed class Repository
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public GitUrl GitUrl { get; private set; }
    public string Branch { get; private set; }
    public LocalPath LocalPath { get; private set; }

    /// <summary>HEAD remote/local после fetch/checkout.</summary>
    public string? CurrentCommit { get; private set; }

    /// <summary>Commit, на котором построен текущий индекс.</summary>
    public string? IndexedCommit { get; private set; }

    public RepositoryStatus Status { get; private set; }
    public bool Enabled { get; private set; }

    public Guid? ProxyId { get; private set; }
    public Guid? CredentialId { get; private set; }

    public int? SyncIntervalSeconds { get; private set; }
    public DateTimeOffset? LastSyncAt { get; private set; }
    public DateTimeOffset? LastIndexAt { get; private set; }
    public string? LastError { get; private set; }

    private Repository(
        Guid id, string name, GitUrl gitUrl, string branch, LocalPath localPath,
        RepositoryStatus status, bool enabled)
    {
        Id = id;
        Name = name;
        GitUrl = gitUrl;
        Branch = branch;
        LocalPath = localPath;
        Status = status;
        Enabled = enabled;
    }

    public static Repository Create(
        Guid id,
        string name,
        string gitUrl,
        string branch,
        string localPath,
        RepositoryStatus status = RepositoryStatus.Active,
        bool enabled = true,
        Guid? proxyId = null,
        Guid? credentialId = null,
        int? syncIntervalSeconds = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Repository Id must not be empty.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Repository Name must not be empty.");
        if (string.IsNullOrWhiteSpace(branch)) throw new DomainValidationException("Branch must not be empty.");
        if (syncIntervalSeconds is not null && syncIntervalSeconds <= 0)
            throw new DomainValidationException("SyncIntervalSeconds must be positive.");

        var git = GitUrl.Create(gitUrl);
        var lp = LocalPath.Create(localPath);

        var repo = new Repository(id, name.Trim(), git, branch.Trim(), lp, status, enabled)
        {
            ProxyId = proxyId,
            CredentialId = credentialId,
            SyncIntervalSeconds = syncIntervalSeconds
        };
        return repo;
    }

    // --- Мутаторы с валидацией ---

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainValidationException("Name must not be empty.");
        Name = name.Trim();
    }

    public void ChangeGitUrl(string gitUrl) => GitUrl = GitUrl.Create(gitUrl);

    public void ChangeBranch(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) throw new DomainValidationException("Branch must not be empty.");
        Branch = branch.Trim();
    }

    public void ChangeLocalPath(string localPath) => LocalPath = LocalPath.Create(localPath);

    public void SetProxy(Guid? proxyId) => ProxyId = proxyId;

    public void SetCredential(Guid? credentialId) => CredentialId = credentialId;

    public void SetSyncInterval(int? seconds)
    {
        if (seconds is not null && seconds <= 0)
            throw new DomainValidationException("SyncIntervalSeconds must be positive.");
        SyncIntervalSeconds = seconds;
    }

    public void SetEnabled(bool enabled) => Enabled = enabled;

    // --- Жизненный цикл статусов ---

    public void MarkSyncing()
    {
        EnsureNotDeleting();
        Status = RepositoryStatus.Syncing;
        LastError = null;
    }

    public void MarkIndexing()
    {
        EnsureNotDeleting();
        Status = RepositoryStatus.Indexing;
        LastError = null;
    }

    public void MarkPaused()
    {
        EnsureNotDeleting();
        Status = RepositoryStatus.Paused;
    }

    public void MarkActive()
    {
        EnsureNotDeleting();
        Status = RepositoryStatus.Active;
    }

    public void MarkError(string error)
    {
        EnsureNotDeleting();
        if (string.IsNullOrWhiteSpace(error)) throw new DomainValidationException("Error message must not be empty.");
        Status = RepositoryStatus.Error;
        LastError = error;
    }

    public void MarkDeleting() => Status = RepositoryStatus.Deleting;

    public void ClearError()
    {
        LastError = null;
        if (Status == RepositoryStatus.Error) Status = RepositoryStatus.Active;
    }

    private void EnsureNotDeleting()
    {
        if (Status == RepositoryStatus.Deleting)
            throw new DomainValidationException("Repository is being deleted; status transition is not allowed.");
    }

    // --- Инвариант IndexedCommit == CurrentCommit после успеха ---

    /// <summary>
    /// Вызывается после успешного fetch+checkout: обновляет CurrentCommit.
    /// </summary>
    public void UpdateCurrentCommit(string? commitSha, DateTimeOffset? at = null)
    {
        if (!string.IsNullOrWhiteSpace(commitSha) && !IsValidCommitSha(commitSha))
            throw new DomainValidationException($"Invalid commit SHA: '{commitSha}'.");
        CurrentCommit = string.IsNullOrWhiteSpace(commitSha) ? null : commitSha.Trim();
        LastSyncAt = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Инвариант: после успешной индексации IndexedCommit == CurrentCommit.
    /// Устанавливает оба в один commit и переводит в Active.
    /// </summary>
    public void MarkIndexSucceeded(string commitSha, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new DomainValidationException("Commit SHA must not be empty on success.");
        if (!IsValidCommitSha(commitSha))
            throw new DomainValidationException($"Invalid commit SHA: '{commitSha}'.");

        var sha = commitSha.Trim();
        CurrentCommit = sha;
        IndexedCommit = sha;
        LastIndexAt = at ?? DateTimeOffset.UtcNow;
        LastSyncAt = LastIndexAt;
        Status = RepositoryStatus.Active;
        LastError = null;

        AssertIndexInvariant();
    }

    /// <summary>Проверка инварианта при завершении джобы: если Indexed != Current — индекс отстал.</summary>
    public void AssertIndexInvariant()
    {
        // Разрешаем null на раннем этапе (ещё не индексировался)
        if (IndexedCommit is null || CurrentCommit is null) return;
        if (!string.Equals(IndexedCommit, CurrentCommit, StringComparison.Ordinal))
            throw new DomainValidationException(
                $"Invariant violated: IndexedCommit ({IndexedCommit}) != CurrentCommit ({CurrentCommit}). Index is stale.");
    }

    /// <summary>Явная проверка без исключения.</summary>
    public bool IsIndexUpToDate() =>
        IndexedCommit is not null && CurrentCommit is not null &&
        string.Equals(IndexedCommit, CurrentCommit, StringComparison.Ordinal);

    private static bool IsValidCommitSha(string sha)
    {
        var s = sha.Trim();
        if (s.Length < 7 || s.Length > 40) return false;
        return s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }
}
