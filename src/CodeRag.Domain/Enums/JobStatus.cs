namespace CodeRag.Domain.Enums;

/// <summary>Статус джобы (indexing_jobs.status).</summary>
public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}
