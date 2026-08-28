using CodeRag.Domain.Entities;

namespace CodeRag.Indexer;

/// <summary>
/// Очередь джоб индексации (indexing.md §4, ADR-008).
/// Channel + SemaphoreSlim(maxConcurrentIndexers) + persistence + CTS per job + Progress.
/// </summary>
public interface IJobRunner
{
    /// <summary>Поставить джобу в очередь (persist Queued + Channel). Возвращает Id.</summary>
    Task<Guid> EnqueueAsync(IndexingJob job, CancellationToken ct = default);

    /// <summary>Отменить queued/running джобу по Id.</summary>
    Task CancelAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Получить джобу по Id (из БД).</summary>
    Task<IndexingJob?> GetAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Список джоб репозитория.</summary>
    Task<IReadOnlyList<IndexingJob>> ListAsync(Guid? repositoryId = null, CancellationToken ct = default);
}
