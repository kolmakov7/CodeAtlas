using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation;
using CodeRag.Documentation.Models;
using CodeRag.Indexer.Services.Indexing;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeRag.Indexer.Services.Handlers;

/// <summary>
/// Whole-repository documentation generation. DB-only job (reads the existing index, writes the
/// documentation table) — runs in parallel with other per-repo jobs and does not take the git lock.
/// Idempotent: regeneration is keyed by content hash, so unchanged targets are skipped unless forced.
/// </summary>
internal sealed class DocumentationJobHandler : IIndexingJobHandler
{
    private const int ProgressThrottleMs = 250;

    public JobType JobType => JobType.Documentation;

    public bool BlocksRepo => false;

    private readonly IRepositoryStore _repoStore;
    private readonly DocumentationService _documentation;
    private readonly JobProgressReporter _progress;
    private readonly ILogger<DocumentationJobHandler> _logger;

    public DocumentationJobHandler(
        IRepositoryStore repoStore,
        DocumentationService documentation,
        JobProgressReporter progress,
        ILogger<DocumentationJobHandler> logger)
    {
        _repoStore = repoStore;
        _documentation = documentation;
        _progress = progress;
        _logger = logger;
    }

    public async Task ExecuteAsync(IndexingJob job, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(job.RepositoryId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"Repository {job.RepositoryId} not found");

        _logger.LogInformation("Documentation job {JobId} repo {Repo}", job.Id, repo.Id);
        await _progress.SetStatsAsync(job, 0.0, phase: "documentation", ct: ct).ConfigureAwait(false);

        var repoTarget = new DocumentationTarget(
            DocumentationTargetType.Repository,
            repo.Id,
            DocumentationTarget.BuildTargetRef(DocumentationTargetType.Repository, null, null, null, null),
            repo.Name,
            RepositoryName: repo.Name,
            IndexedCommit: repo.IndexedCommit,
            Branch: repo.Branch);

        var progress = new ThrottledProgress(_progress, job, ct);
        var options = ParseOptions(job.Options);
        var result = await _documentation.GenerateForRepositoryAsync(
            repoTarget, includeRepositoryLevel: true, force: options.Force, maxSymbols: options.MaxSymbols,
            progress: progress, ct: ct,
            strategy: string.IsNullOrWhiteSpace(options.Strategy) ? "usage" : options.Strategy!,
            minUsage: options.MinUsage).ConfigureAwait(false);
        progress.Complete().GetAwaiter().GetResult();

        var generated = result.Results.Count(r => r.Generated);
        var skipped = Math.Max(0, result.Succeeded - generated); // up-to-date, not regenerated
        await _progress.SetStatsAsync(job, 1.0, result.Total, result.Total, "documentation", null, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Documentation job {JobId} repo {Repo}: {Total} target(s), {Generated} generated, {Skipped} unchanged, {Failed} failed",
            job.Id, repo.Id, result.Total, generated, skipped, result.Failed);
    }

    /// <summary>Per-job options (indexing_jobs.options JSON): force regeneration and/or cap the number of targets.</summary>
    private sealed record DocJobOptions(bool Force = false, int? MaxSymbols = null, string? Strategy = null, int MinUsage = 0);

    private static readonly JsonSerializerOptions JobJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static DocJobOptions ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DocJobOptions();
        try { return JsonSerializer.Deserialize<DocJobOptions>(json, JobJsonOptions) ?? new DocJobOptions(); }
        catch { return new DocJobOptions(); }
    }

    /// <summary>IProgress with throttling (250 ms) — does not hit the DB on every target.</summary>
    private sealed class ThrottledProgress(JobProgressReporter target, IndexingJob job, CancellationToken ct) : IProgress<(int Done, int Total)>
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _lastSave = DateTime.MinValue;
        private volatile bool _finished;

        public void Report((int Done, int Total) value)
        {
            if (_finished) return;
            var isFinal = value.Total > 0 && value.Done >= value.Total;
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var now = DateTime.UtcNow;
                    if (!isFinal && (now - _lastSave).TotalMilliseconds < ProgressThrottleMs) return;
                    _lastSave = now;
                    var total = Math.Max(1, value.Total);
                    var done = Math.Min(value.Done, total);
                    await target.SetStatsAsync(job, (double)done / total, total, done, "documentation", null, ct).ConfigureAwait(false);
                }
                catch { /* progress is non-critical */ }
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
