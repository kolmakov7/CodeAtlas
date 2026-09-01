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

    public void MarkRunning()
    {
        if (Status != JobStatus.Queued) throw new DomainValidationException($"Job {Id} can only run from Queued, current {Status}.");
        Status = JobStatus.Running;
    }

    public void UpdateProgress(double progress)
    {
        if (progress is < 0 or > 1) throw new DomainValidationException("Progress must be 0..1.");
        Progress = progress;
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

    internal static IndexingJob Rehydrate(
        Guid id, Guid repositoryId, JobType type, JobStatus status,
        DateTimeOffset startedAt, DateTimeOffset? finishedAt,
        double progress, string? error, string? fromCommit, string? toCommit)
    {
        return new IndexingJob(id, repositoryId, type, status, startedAt)
        {
            FinishedAt = finishedAt,
            Progress = progress,
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
            FromCommit = string.IsNullOrWhiteSpace(fromCommit) ? null : fromCommit.Trim(),
            ToCommit = string.IsNullOrWhiteSpace(toCommit) ? null : toCommit.Trim()
        };
    }
}
