using CodeRag.Analyzers.Abstractions;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;

namespace CodeRag.Indexer.Services.Handlers;

/// <summary>
/// Отдельная фоновая джоба whole-repository извлечения связей (вынесена из Index).
/// Идёт параллельно с Embedding, не растягивает Index-джобу. Идемпотентна:
/// на старте берёт commit (ToCommit ?? IndexedCommit), извлекает, и если репо
/// уже поехал дальше — просто не сохраняет (новый Index поставит свежую Relations-джобу).
/// </summary>
internal sealed class RelationsJobHandler : IIndexingJobHandler
{
    private const int ProgressThrottleMs = 250;

    public JobType JobType => JobType.Relations;

    /// <summary>Читает git (ls-tree/show) и перезаписывает relations — git-лок не нужен.</summary>
    public bool BlocksRepo => false;

    private readonly IRepositoryStore _repoStore;
    private readonly RepositoryRelationService _relations;
    private readonly JobProgressReporter _progress;
    private readonly ILogger<RelationsJobHandler> _logger;

    public RelationsJobHandler(
        IRepositoryStore repoStore,
        RepositoryRelationService relations,
        JobProgressReporter progress,
        ILogger<RelationsJobHandler> logger)
    {
        _repoStore = repoStore;
        _relations = relations;
        _progress = progress;
        _logger = logger;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        var commit = job.ToCommit ?? repo.IndexedCommit;
        if (string.IsNullOrWhiteSpace(commit))
            throw new InvalidOperationException($"Relations job {job.Id}: repo {repo.Id} has no IndexedCommit and job has no ToCommit");

        _logger.LogInformation("Relations job {JobId} repo {Repo} commit {Commit}", job.Id, repo.Id, commit);
        await _progress.SetStatsAsync(job, 0.0, phase: "relations", ct: ct).ConfigureAwait(false);

        var progress = new ThrottledProgress(_progress, job, ct);
        var results = await _relations.ExtractAsync(repo, commit, progress, ct).ConfigureAwait(false);
        progress.Complete().GetAwaiter().GetResult();

        // Staleness-guard: репо мог переиндексироваться (новый commit), пока мы извлекали.
        var fresh = await _repoStore.GetByIdAsync(repo.Id, ct).ConfigureAwait(false);
        if (fresh is not null
            && fresh.IndexedCommit is not null
            && !string.Equals(fresh.IndexedCommit, commit, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Relations job {JobId}: repo {Repo} moved {Commit} -> {NewCommit}; discarding extraction",
                job.Id, repo.Id, commit, fresh.IndexedCommit);
            await _progress.SetStatsAsync(job, 1.0, phase: "stale", ct: ct).ConfigureAwait(false);
            return;
        }

        await _relations.PersistAsync(repo, results, ct).ConfigureAwait(false);

        var totalFiles = results.Sum(r => r.FilesProcessed);
        var totalRelations = results.Sum(r => r.Relations.Count);
        await _progress.SetStatsAsync(job, 1.0, totalFiles, totalFiles, "commit", null, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Relations job {JobId} repo {Repo}: {Languages} language(s), {Files} files -> {Relations} relations",
            job.Id, repo.Id, results.Count, totalFiles, totalRelations);
    }

    /// <summary>IProgress с троттлингом (250 мс) — не долбит БД на каждый файл.</summary>
    private sealed class ThrottledProgress(JobProgressReporter target, IndexingJob job, CancellationToken ct) : IProgress<RelationExtractionProgress>
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _lastSave = DateTime.MinValue;
        private volatile bool _finished;

        public void Report(RelationExtractionProgress value)
        {
            if (_finished) return;
            var isFinal = value.FilesDone >= value.FilesTotal;
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var now = DateTime.UtcNow;
                    if (!isFinal && (now - _lastSave).TotalMilliseconds < ProgressThrottleMs) return;
                    _lastSave = now;
                    var total = Math.Max(1, value.FilesTotal);
                    var done = Math.Min(value.FilesDone, total);
                    await target.SetStatsAsync(job, (double)done / total, total, done, "relations", value.CurrentFile, ct).ConfigureAwait(false);
                }
                catch { /* прогресс не критичен */ }
                finally { _gate.Release(); }
            });
        }

        public Task Complete()
        {
            _finished = true;
            return Task.CompletedTask;
        }
    }
}
