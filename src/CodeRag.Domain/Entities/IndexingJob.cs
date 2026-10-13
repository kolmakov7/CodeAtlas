using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Джоба индексации (таблица indexing_jobs, indexing.md §4).</summary>
public sealed class IndexingJob
{
    public Guid Id { get; private set; }
    public Guid RepositoryId { get; private set; }
    public JobType Type { get; private set; }
    public JobStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public string? FromCommit { get; private set; }
    public string? ToCommit { get; private set; }

    /// <summary>Всего единиц работы в джобе (файлы для Index/Rebuild, чанки для Embedding). Null — неизвестно/не применимо.</summary>
    public int? TotalItems { get; private set; }
    /// <summary>Обработано единиц работы.</summary>
    public int? ItemsDone { get; private set; }
    /// <summary>Текущий этап: clearing/diff/clone/fetch/indexing/relations/embedding/commit.</summary>
    public string? Phase { get; private set; }
    /// <summary>Последний обработанный путь (для файловых джоб).</summary>
    public string? CurrentFile { get; private set; }

    private IndexingJob(Guid id, Guid repositoryId, JobType type, JobStatus status, DateTimeOffset startedAt)
    {
        Id = id;
        RepositoryId = repositoryId;
        Type = type;
        Status = status;
        StartedAt = startedAt;
    }

    public static IndexingJob Create(
        Guid id, Guid repositoryId, JobType type,
        JobStatus status = JobStatus.Queued,
        DateTimeOffset? startedAt = null,
        string? fromCommit = null, string? toCommit = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Job Id must not be empty.");
        if (repositoryId == Guid.Empty) throw new DomainValidationException("RepositoryId must not be empty.");

        return new IndexingJob(id, repositoryId, type, status, startedAt ?? DateTimeOffset.UtcNow)
        {
            FromCommit = string.IsNullOrWhiteSpace(fromCommit) ? null : fromCommit.Trim(),
            ToCommit = string.IsNullOrWhiteSpace(toCommit) ? null : toCommit.Trim(),
            Progress = 0
        };
    }

    public void MarkRunning(DateTimeOffset? at = null)
    {
        if (Status != JobStatus.Queued) throw new DomainValidationException($"Job {Id} can only run from Queued, current {Status}.");
        Status = JobStatus.Running;
        // StartedAt фиксирует фактический старт выполнения (а не момент постановки в
        // очередь): иначе скорость/ETA джобы включают время ожидания в очереди, слоте
        // семафора и per-repo локе и получаются заниженными (embedding-джоба, которая
        // создаётся в конце Index и может долго ждать, страдала сильнее всего).
        StartedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void UpdateProgress(double progress)
    {
        if (progress is < 0 or > 1) throw new DomainValidationException("Progress must be 0..1.");
        Progress = progress;
    }

    /// <summary>
    /// Обновить прогресс + детали (total/done/phase/current_file). Null-значения не перезаписывают
    /// существующие — удобно вызывать с частичными обновлениями из параллельных воркеров.
    /// </summary>
    public void SetProgress(double progress, int? totalItems = null, int? itemsDone = null, string? phase = null, string? currentFile = null)
    {
        if (progress is < 0 or > 1) throw new DomainValidationException("Progress must be 0..1.");
        Progress = progress;
        if (totalItems.HasValue) TotalItems = totalItems;
        if (itemsDone.HasValue) ItemsDone = itemsDone;
        if (!string.IsNullOrEmpty(phase)) Phase = phase;
        if (currentFile is not null) CurrentFile = currentFile;
    }

    public void MarkCompleted(DateTimeOffset? at = null)
    {
        if (Status != JobStatus.Running) throw new DomainValidationException($"Job {Id} can only complete from Running.");
        Status = JobStatus.Completed;
        FinishedAt = at ?? DateTimeOffset.UtcNow;
        Progress = 1;
    }

    public void MarkFailed(string error, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(error)) throw new DomainValidationException("Error must not be empty.");
        Status = JobStatus.Failed;
        Error = error;
        FinishedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void MarkCancelled(DateTimeOffset? at = null)
    {
        Status = JobStatus.Cancelled;
        FinishedAt = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Возврат в очередь (рестарт в середине Running — джобу догоняем заново, она идемпотентна).</summary>
    public void Requeue()
    {
        Status = JobStatus.Queued;
        FinishedAt = null;
        Error = null;
        Progress = 0;
        TotalItems = null;
        ItemsDone = null;
        Phase = null;
        CurrentFile = null;
    }

    public static IndexingJob Rehydrate(
        Guid id, Guid repositoryId, JobType type, JobStatus status,
        DateTimeOffset startedAt, DateTimeOffset? finishedAt,
        double progress, string? error, string? fromCommit, string? toCommit,
        int? totalItems = null, int? itemsDone = null, string? phase = null, string? currentFile = null)
    {
        return new IndexingJob(id, repositoryId, type, status, startedAt)
        {
            FinishedAt = finishedAt,
            Progress = progress,
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
            FromCommit = string.IsNullOrWhiteSpace(fromCommit) ? null : fromCommit.Trim(),
            ToCommit = string.IsNullOrWhiteSpace(toCommit) ? null : toCommit.Trim(),
            TotalItems = totalItems,
            ItemsDone = itemsDone,
            Phase = phase,
            CurrentFile = currentFile
        };
    }
}
