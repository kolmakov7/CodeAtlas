namespace CodeRag.Domain.Enums;

/// <summary>Статус жизненного цикла репозитория (storage.md §2, indexing.md §4).</summary>
public enum RepositoryStatus
{
    Active = 0,
    Syncing = 1,
    Indexing = 2,
    Paused = 3,
    Error = 4,
    Deleting = 5
}
