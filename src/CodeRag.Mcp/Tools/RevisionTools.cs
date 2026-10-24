using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Domain.Models;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public sealed class RevisionTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly RevisionService _revision;
    private readonly ConsolidationService _consolidation;
    private readonly IMcpUsageRecorder? _usage;
    private readonly ILogger<RevisionTools> _logger;

    public RevisionTools(
        RevisionService revision,
        ConsolidationService consolidation,
        ILogger<RevisionTools> logger,
        IMcpUsageRecorder? usage = null)
    {
        _revision = revision;
        _consolidation = consolidation;
        _logger = logger;
        _usage = usage;
    }

    private async Task<string> TrackAsync(string tool, Guid? repositoryId, Func<Task<string>> execute)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try { result = await execute().ConfigureAwait(false); }
        catch (Exception ex) { result = $"Error: {ex.Message}"; }
        sw.Stop();
        _usage?.Record(tool, !result.StartsWith("Error:", StringComparison.Ordinal), (int)sw.ElapsedMilliseconds, repositoryId);
        return result;
    }

    [McpServerTool, Description("Run deterministic knowledge revision: find duplicate/similar entries, contradictions, missing sources and stale records, and enqueue consolidation jobs. Does NOT call the LLM and never applies changes. Safe to repeat: identical inputs do not create duplicate jobs.")]
    public Task<string> RunRevisionAsync(
        [Description("Project Id (Guid); omit for all")] Guid? projectId = null,
        [Description("Repository Id (Guid); omit for all")] Guid? repositoryId = null,
        [Description("Preview candidates without writing jobs")] bool dryRun = false,
        CancellationToken ct = default)
        => TrackAsync("run_revision", repositoryId, async () =>
        {
            var result = await _revision.RunAsync(projectId, repositoryId, "manual", dryRun, ct).ConfigureAwait(false);
            var sb = new StringBuilder($"Revision {(dryRun ? "(dry run) " : "")}run {result.RunId}: " +
                                       $"scanned={result.Scanned} created={result.JobsCreated} skipped={result.JobsSkipped}");
            if (result.Warnings.Count > 0) sb.AppendLine().AppendLine("Warnings: " + string.Join("; ", result.Warnings));
            if (result.JobIds.Count > 0) sb.AppendLine().AppendLine("Jobs: " + string.Join(", ", result.JobIds));
            return sb.ToString();
        });

    [McpServerTool, Description("List consolidation queue jobs with optional filters. Shows LLM-independent state.")]
    public Task<string> ListRevisionJobsAsync(
        [Description("pending|ready_for_analysis|awaiting_llm|ready_for_review|accepted|rejected|cancelled|error|postponed|undone")] string? status = null,
        [Description("duplicates|contradiction|missing_source|stale|consolidate")] string? type = null,
        [Description("Project Id (Guid)")] Guid? projectId = null,
        [Description("Repository Id (Guid)")] Guid? repositoryId = null,
        [Description("Max results")] int limit = 50,
        CancellationToken ct = default)
        => TrackAsync("list_revision_jobs", repositoryId, async () =>
        {
            var jobs = await _consolidation.ListAsync(new ConsolidationJobQuery(
                ParseStatus(status), ParseType(type), projectId, repositoryId, limit), ct).ConfigureAwait(false);
            if (jobs.Count == 0) return "No revision jobs found";
            return string.Join("\n", jobs.Select(FormatJob));
        });

    [McpServerTool, Description("Get a consolidation job with its source entries and proposal/analysis.")]
    public Task<string> GetRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId, CancellationToken ct = default)
        => TrackAsync("get_revision_job", null, async () =>
        {
            var detail = await _consolidation.GetDetailAsync(jobId, ct).ConfigureAwait(false);
            var sb = new StringBuilder(FormatJob(detail.Job));
            sb.AppendLine().AppendLine("Sources:");
            foreach (var item in detail.Items)
                sb.AppendLine($"- {item.Id} v{item.Version} [{item.Status.ToString().ToLowerInvariant()}] {item.Type.ToString().ToLowerInvariant()} — {item.Title}");
            if (!string.IsNullOrWhiteSpace(detail.Job.Analysis))
                sb.AppendLine().AppendLine("Analysis:\n" + detail.Job.Analysis);
            if (!string.IsNullOrWhiteSpace(detail.Job.Proposal))
                sb.AppendLine().AppendLine("Proposal:\n" + detail.Job.Proposal);
            return sb.ToString();
        });

    [McpServerTool, Description("Run the LLM analysis for one consolidation job on explicit user command. When the LLM is not configured/unavailable the job moves to awaiting_llm or error and is NOT retried automatically.")]
    public Task<string> AnalyzeRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("analyze_revision_job", null, async () =>
        {
            var result = await _consolidation.AnalyzeAsync(jobId, actor, ct).ConfigureAwait(false);
            return $"Job {result.JobId} status={result.Status} llmConfigured={result.LlmConfigured}" +
                   (result.Error is null ? "" : $"\nError: {result.Error}") +
                   (result.Proposal is null ? "" : "\nProposal:\n" + result.Proposal);
        });

    [McpServerTool, Description("Accept a consolidation proposal. Re-checks source versions/hashes before applying; supersedes originals without deleting them and records history. Requires the current proposalVersion.")]
    public Task<string> AcceptRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Expected proposal version")] int expectedProposalVersion,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("accept_revision_job", null, async () =>
        {
            var result = await _consolidation.AcceptAsync(jobId, expectedProposalVersion, actor, ct)
                .ConfigureAwait(false);
            return FormatOutcome(result);
        });

    [McpServerTool, Description("Reject a consolidation proposal. Source entries are left untouched.")]
    public Task<string> RejectRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Reason")] string? reason = null,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("reject_revision_job", null, async () =>
            FormatOutcome(await _consolidation.RejectAsync(jobId, reason, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Cancel a consolidation job without changing knowledge.")]
    public Task<string> CancelRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("cancel_revision_job", null, async () =>
            FormatOutcome(await _consolidation.CancelAsync(jobId, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Postpone a consolidation decision; leaves the queue item resolvable later.")]
    public Task<string> PostponeRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("postpone_revision_job", null, async () =>
            FormatOutcome(await _consolidation.PostponeAsync(jobId, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Reopen a failed/awaiting job for another explicit analysis. Does not call the LLM by itself.")]
    public Task<string> RetryRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("retry_revision_job", null, async () =>
            FormatOutcome(await _consolidation.RetryAsync(jobId, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Undo an accepted consolidation: restore source entries, archive the merged entry and record history. Does not delete data.")]
    public Task<string> UndoRevisionJobAsync(
        [Description("Job Id (Guid)")] Guid jobId,
        [Description("Author label")] string? actor = null,
        CancellationToken ct = default)
        => TrackAsync("undo_revision_job", null, async () =>
            FormatOutcome(await _consolidation.UndoAsync(jobId, actor, ct).ConfigureAwait(false)));

    [McpServerTool, Description("Show revision status: LLM dependency state, last run, and queue depth. LLM being unconfigured is a dependency state, not a server failure.")]
    public Task<string> RevisionStatusAsync(CancellationToken ct = default)
        => TrackAsync("revision_status", null, async () =>
        {
            var last = await _revision.GetLatestRunAsync(ct).ConfigureAwait(false);
            var ready = await _consolidation.CountAsync(
                new ConsolidationJobQuery(Status: ConsolidationJobStatus.ReadyForAnalysis), ct).ConfigureAwait(false);
            var awaiting = await _consolidation.CountAsync(
                new ConsolidationJobQuery(Status: ConsolidationJobStatus.AwaitingLlm), ct).ConfigureAwait(false);
            var review = await _consolidation.CountAsync(
                new ConsolidationJobQuery(Status: ConsolidationJobStatus.ReadyForReview), ct).ConfigureAwait(false);
            var errors = await _consolidation.CountAsync(
                new ConsolidationJobQuery(Status: ConsolidationJobStatus.Error), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                llm = new { configured = _consolidation.LlmConfigured, status = _consolidation.LlmConfigured ? "configured" : "not_configured" },
                lastRun = last is null ? null : new { last.Id, last.Trigger, last.Status, last.StartedAt, last.FinishedAt, last.Error },
                queue = new { readyForAnalysis = ready, awaitingLlm = awaiting, readyForReview = review, errors }
            }, JsonOptions);
        });

    private static string FormatJob(ConsolidationJob job)
        => $"Job {job.Id} v{job.Version} [{job.Status.ToString().ToLowerInvariant()}] {job.Type.ToString().ToLowerInvariant()} " +
           $"attempts={job.Attempts} proposalVersion={job.ProposalVersion} — {job.Reason}" +
           (job.LastError is null ? "" : $"\n  lastError: {job.LastError}");

    private static string FormatOutcome(ConsolidationApplyOutcome result)
        => result.Success
            ? $"OK job={result.JobId} status={result.Status}" + (result.ResultKnowledgeId is null ? "" : $" result={result.ResultKnowledgeId}")
            : $"Error: {result.Error} (status={result.Status})";

    private static ConsolidationJobStatus? ParseStatus(string? value)
        => Enum.TryParse<ConsolidationJobStatus>(value, true, out var parsed) ? parsed : null;

    private static ConsolidationJobType? ParseType(string? value)
        => Enum.TryParse<ConsolidationJobType>(value, true, out var parsed) ? parsed : null;
}
