using System.Collections.Concurrent;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git;
using CodeRag.Git.Models;
using CodeRag.Indexer;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Indexer.Services.Handlers;

internal sealed class IndexJobHandler : IIndexingJobHandler
{
    public JobType JobType => JobType.Index;

    private readonly IRepositoryStore _repoStore;
    private readonly IGitService _git;
    private readonly IJobRunner _jobRunner;
    private readonly JobProgressReporter _progress;
    private readonly FileIndexer _fileIndexer;
    private readonly CSharpRelationService _relationService;
    private readonly IOptionsMonitor<IndexerOptions> _options;
    private readonly ILogger<IndexJobHandler> _logger;

    public IndexJobHandler(
        IRepositoryStore repoStore,
        IGitService git,
        IJobRunner jobRunner,
        JobProgressReporter progress,
        FileIndexer fileIndexer,
        CSharpRelationService relationService,
        IOptionsMonitor<IndexerOptions> options,
        ILogger<IndexJobHandler> logger)
    {
        _repoStore = repoStore;
        _git = git;
        _jobRunner = jobRunner;
        _progress = progress;
        _fileIndexer = fileIndexer;
        _relationService = relationService;
        _options = options;
        _logger = logger;
    }

    public Task ExecuteAsync(IndexingJob job, CancellationToken ct) => ExecuteCoreAsync(job, null, ct);

    public async Task ExecuteCoreAsync(IndexingJob job, GitDiff? precomputedDiff, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        repo.MarkIndexing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        var from = job.FromCommit ?? repo.IndexedCommit;
        var to = job.ToCommit ?? repo.CurrentCommit;
        if (string.IsNullOrWhiteSpace(to))
            to = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);

        var diff = precomputedDiff ?? await _git.DiffAsync(repo.LocalPath.Value, from, to, ct).ConfigureAwait(false);
        diff = IndexingHelper.ApplyDirectoryFilter(repo, diff);
        var total = diff.Entries.Count;
        if (total == 0)
        {
            _logger.LogInformation("Index job {JobId}: empty diff (after filter), nothing to do", job.Id);
            await IndexingHelper.CommitIndexedAsync(_repoStore, repo, to!, ct).ConfigureAwait(false);
            return;
        }

        await _progress.SetStatsAsync(job, 0.0, total, 0, "indexing", null, ct).ConfigureAwait(false);

        var processed = 0;
        var csharpTouched = 0;
        var lastProgressSave = DateTime.UtcNow;
        using var progressGate = new SemaphoreSlim(1, 1);
        var pendingChunks = new ConcurrentQueue<Domain.Entities.Chunk>();
        var degree = Math.Max(1, _options.CurrentValue.ParallelFileIndexers);

        await Parallel.ForEachAsync(diff.Entries, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct }, async (cf, token) =>
        {
            if (IndexingHelper.IsCSharpPath(cf.Path) || (cf.Kind == GitDiffKind.Renamed && IndexingHelper.IsCSharpPath(cf.OldPath)))
                Interlocked.Exchange(ref csharpTouched, 1);

            if (cf.Kind == GitDiffKind.Deleted)
                await _fileIndexer.DeleteFileAsync(repo, cf.Path, token).ConfigureAwait(false);
            else
            {
                if (cf.Kind == GitDiffKind.Renamed && !string.IsNullOrWhiteSpace(cf.OldPath))
                    await _fileIndexer.DeleteFileAsync(repo, cf.OldPath!, token).ConfigureAwait(false);
                await _fileIndexer.IndexFileAsync(repo, cf.Path, to!, pendingChunks, token).ConfigureAwait(false);
            }

            var done = Interlocked.Increment(ref processed);
            await progressGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                if (done % 10 == 0 || (now - lastProgressSave).TotalSeconds >= 1)
                {
                    var p = (double)done / total;
                    job.SetProgress(p, total, done, "indexing", cf.Path);
                    await _progress.SetStatsAsync(job, p, total, done, "indexing", cf.Path, token).ConfigureAwait(false);
                    lastProgressSave = now;
                }
            }
            finally { progressGate.Release(); }
        });

        if (pendingChunks.Count > 0)
        {
            var embeddingJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Embedding, JobStatus.Queued, DateTimeOffset.UtcNow, to, to);
            await _jobRunner.EnqueueAsync(embeddingJob, ct).ConfigureAwait(false);
            _logger.LogInformation("Index job {JobId} enqueued Embedding job {EmbedJob} ({Count} new chunks)", job.Id, embeddingJob.Id, pendingChunks.Count);
        }

        if (csharpTouched == 1)
        {
            await _progress.SetStatsAsync(job, job.Progress, total, total, "relations", null, ct).ConfigureAwait(false);
            try { await _relationService.RebuildAsync(repo, to!, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "C# relation extraction failed for repo {Repo}; keeping existing relations", repo.Id); }
        }

        await IndexingHelper.CommitIndexedAsync(_repoStore, repo, to!, ct).ConfigureAwait(false);
        await _progress.SetStatsAsync(job, 1.0, total, total, "commit", null, ct).ConfigureAwait(false);
    }
}
