using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;

namespace CodeRag.Domain.Ports;

public interface IIndexingJobStore
{
    Task UpsertAsync(IndexingJob job, CancellationToken ct = default);
    Task<IndexingJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<IndexingJob>> ListAsync(Guid? repositoryId = null, CancellationToken ct = default);
    Task<IReadOnlyList<IndexingJob>> ListByStatusAsync(JobStatus status, CancellationToken ct = default);
    Task UpdateProgressAsync(Guid jobId, double progress, CancellationToken ct = default);
    Task UpdateProgressAsync(IndexingJob job, CancellationToken ct = default);
}
