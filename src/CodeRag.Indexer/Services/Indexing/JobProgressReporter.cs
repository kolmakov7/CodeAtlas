using CodeRag.Domain.Entities;
using CodeRag.Indexer;

namespace CodeRag.Indexer.Services.Indexing;

internal sealed class JobProgressReporter
{
    private readonly IndexingJobStore _store;
    public JobProgressReporter(IndexingJobStore store) => _store = store;

    public Task SetProgressAsync(IndexingJob job, double progress, CancellationToken ct)
    {
        job.UpdateProgress(progress);
        return _store.UpdateProgressAsync(job.Id, progress, ct);
    }

    public Task SetStatsAsync(IndexingJob job, double progress, int? totalItems = null, int? itemsDone = null, string? phase = null, string? currentFile = null, CancellationToken ct = default)
    {
        job.SetProgress(progress, totalItems, itemsDone, phase, currentFile);
        return _store.UpdateProgressAsync(job, ct);
    }
}
