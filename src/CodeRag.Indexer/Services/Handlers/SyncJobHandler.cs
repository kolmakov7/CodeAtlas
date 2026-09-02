using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git;
using CodeRag.Indexer;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Handlers;

internal sealed class SyncJobHandler : IIndexingJobHandler
{
    public JobType JobType => JobType.Sync;

    private readonly IRepositoryStore _repoStore;
    private readonly IGitService _git;
    private readonly IJobRunner _jobRunner;
    private readonly JobProgressReporter _progress;
    private readonly GitCredentialResolver _credResolver;
    private readonly ILogger<SyncJobHandler> _logger;

    public SyncJobHandler(
        IRepositoryStore repoStore,
        IGitService git,
        IJobRunner jobRunner,
        JobProgressReporter progress,
        GitCredentialResolver credResolver,
        ILogger<SyncJobHandler> logger)
    {
        _repoStore = repoStore;
        _git = git;
        _jobRunner = jobRunner;
        _progress = progress;
        _credResolver = credResolver;
        _logger = logger;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        if (!repo.Enabled || repo.Status == RepositoryStatus.Paused)
        {
            _logger.LogInformation("Sync skipped repo {Repo} status={Status} enabled={Enabled}", repo.Id, repo.Status, repo.Enabled);
            await _progress.SetProgressAsync(job, 1.0, ct).ConfigureAwait(false);
            return;
        }

        repo.MarkSyncing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

        var (proxy, cred) = await _credResolver.ResolveAsync(repo, ct).ConfigureAwait(false);

        try
        {
            await _progress.SetStatsAsync(job, 0.25, phase: "fetch", ct: ct).ConfigureAwait(false);
            if (!Directory.Exists(repo.LocalPath.Value) || !Directory.Exists(Path.Combine(repo.LocalPath.Value, ".git")))
            {
                _logger.LogInformation("Sync: local path {Path} not cloned, cloning branch {Branch} via proxy/cred", repo.LocalPath.Value, repo.Branch);
                await _progress.SetStatsAsync(job, 0.10, phase: "clone", ct: ct).ConfigureAwait(false);
                await _git.CloneAsync(repo.GitUrl.Value, repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            else
            {
                await _git.FetchAsync(repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            var newHead = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);

            repo.UpdateCurrentCommit(newHead);
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);

            if (string.Equals(newHead, repo.IndexedCommit, StringComparison.Ordinal))
            {
                _logger.LogInformation("Sync done repo {Repo}: HEAD {Head} already indexed", repo.Id, newHead);
                repo.MarkActive();
                await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
                await _progress.SetProgressAsync(job, 1.0, ct).ConfigureAwait(false);
                return;
            }

            await _progress.SetStatsAsync(job, 0.6, phase: "diff", ct: ct).ConfigureAwait(false);
            var diff = await _git.DiffAsync(repo.LocalPath.Value, repo.IndexedCommit, newHead, ct).ConfigureAwait(false);
            _logger.LogInformation("Sync diff repo {Repo}: {Count} changed files (full={Full})", repo.Id, diff.Entries.Count, diff.IsFullDiff);

            var indexJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Index, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, newHead);
            await _jobRunner.EnqueueAsync(indexJob, ct).ConfigureAwait(false);
            _logger.LogInformation("Sync repo {Repo} enqueued Index job {IndexJob} ({From} -> {To})", repo.Id, indexJob.Id, repo.IndexedCommit, newHead);

            repo.MarkActive();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            await _progress.SetProgressAsync(job, 1.0, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            repo.MarkError(IndexingHelper.TruncateError(ex.Message));
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            throw;
        }
    }
}
