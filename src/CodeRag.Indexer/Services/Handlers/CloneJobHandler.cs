using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git;
using CodeRag.Indexer;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Handlers;

internal sealed class CloneJobHandler : IIndexingJobHandler
{
    public JobType JobType => JobType.Clone;

    private readonly IRepositoryStore _repoStore;
    private readonly IGitService _git;
    private readonly IJobRunner _jobRunner;
    private readonly JobProgressReporter _progress;
    private readonly GitCredentialResolver _credResolver;
    private readonly ILogger<CloneJobHandler> _logger;

    public CloneJobHandler(
        IRepositoryStore repoStore,
        IGitService git,
        IJobRunner jobRunner,
        JobProgressReporter progress,
        GitCredentialResolver credResolver,
        ILogger<CloneJobHandler> logger)
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
            _logger.LogInformation("Clone skipped repo {Repo} status={Status}", repo.Id, repo.Status);
            await _progress.SetProgressAsync(job, 1.0, ct).ConfigureAwait(false);
            return;
        }

        repo.MarkSyncing();
        await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
        var (proxy, cred) = await _credResolver.ResolveAsync(repo, ct).ConfigureAwait(false);

        try
        {
            await _progress.SetStatsAsync(job, 0.10, phase: "clone", ct: ct).ConfigureAwait(false);
            if (!Directory.Exists(Path.Combine(repo.LocalPath.Value, ".git")))
            {
                _logger.LogInformation("Clone job {JobId} cloning {Url} -> {Path} branch {Branch}", job.Id, repo.GitUrl.Value, repo.LocalPath.Value, repo.Branch);
                await _git.CloneAsync(repo.GitUrl.Value, repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Clone job {JobId} already cloned, fetching instead", job.Id);
                await _git.FetchAsync(repo.LocalPath.Value, repo.Branch, proxy, cred, ct).ConfigureAwait(false);
            }

            await _progress.SetStatsAsync(job, 0.70, phase: "head", ct: ct).ConfigureAwait(false);
            var head = await _git.GetHeadAsync(repo.LocalPath.Value, repo.Branch, ct).ConfigureAwait(false);
            repo.UpdateCurrentCommit(head);
            repo.MarkActive();
            repo.ClearError();
            await _repoStore.UpsertAsync(repo, ct).ConfigureAwait(false);
            _logger.LogInformation("Clone job {JobId} done head {Head}", job.Id, head);

            var indexJob = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Index, JobStatus.Queued, DateTimeOffset.UtcNow, repo.IndexedCommit, head);
            await _jobRunner.EnqueueAsync(indexJob, ct).ConfigureAwait(false);
            _logger.LogInformation("Clone enqueued Index job {IndexJob}", indexJob.Id);

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
