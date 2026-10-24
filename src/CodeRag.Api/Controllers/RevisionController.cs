using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/revision")]
public sealed class RevisionController : ControllerBase
{
    private readonly RevisionService _revision;
    private readonly ConsolidationService _consolidation;

    public RevisionController(RevisionService revision, ConsolidationService consolidation)
    {
        _revision = revision;
        _consolidation = consolidation;
    }

    [HttpPost("run")]
    public async Task<ActionResult<RevisionRunResult>> Run(
        [FromBody] RunRevisionRequest request, CancellationToken ct)
        => Ok(await _revision.RunAsync(request.ProjectId, request.RepositoryId, "manual", request.DryRun, ct));

    [HttpGet("jobs")]
    public async Task<ActionResult<IReadOnlyList<ConsolidationJob>>> ListJobs(
        [FromQuery] ConsolidationJobStatus? status,
        [FromQuery] ConsolidationJobType? type,
        [FromQuery] Guid? projectId,
        [FromQuery] Guid? repositoryId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
        => Ok(await _consolidation.ListAsync(
            new ConsolidationJobQuery(status, type, projectId, repositoryId, limit, offset), ct));

    [HttpGet("jobs/{id:guid}")]
    public async Task<ActionResult<ConsolidationJobDetail>> GetJob(Guid id, CancellationToken ct)
        => Ok(await _consolidation.GetDetailAsync(id, ct));

    [HttpPost("jobs/{id:guid}/analyze")]
    public async Task<ActionResult<ConsolidationAnalysisResult>> Analyze(
        Guid id, [FromBody] RevisionActorRequest? request, CancellationToken ct)
        => Ok(await _consolidation.AnalyzeAsync(id, request?.Actor, ct));

    [HttpPost("jobs/{id:guid}/accept")]
    public async Task<ActionResult<ConsolidationApplyOutcome>> Accept(
        Guid id, [FromBody] AcceptConsolidationRequest request, CancellationToken ct)
        => Ok(await _consolidation.AcceptAsync(id, request.ExpectedProposalVersion, request.Actor, ct));

    [HttpPost("jobs/{id:guid}/reject")]
    public async Task<ActionResult<ConsolidationApplyOutcome>> Reject(
        Guid id, [FromBody] RevisionActorRequest? request, CancellationToken ct)
        => Ok(await _consolidation.RejectAsync(id, request?.Reason, request?.Actor, ct));

    [HttpPost("jobs/{id:guid}/cancel")]
    public async Task<ActionResult<ConsolidationApplyOutcome>> Cancel(
        Guid id, [FromBody] RevisionActorRequest? request, CancellationToken ct)
        => Ok(await _consolidation.CancelAsync(id, request?.Actor, ct));

    [HttpPost("jobs/{id:guid}/postpone")]
    public async Task<ActionResult<ConsolidationApplyOutcome>> Postpone(
        Guid id, [FromBody] RevisionActorRequest? request, CancellationToken ct)
        => Ok(await _consolidation.PostponeAsync(id, request?.Actor, ct));

    [HttpPost("jobs/{id:guid}/retry")]
    public async Task<ActionResult<ConsolidationApplyOutcome>> Retry(
        Guid id, [FromBody] RevisionActorRequest? request, CancellationToken ct)
        => Ok(await _consolidation.RetryAsync(id, request?.Actor, ct));

    [HttpPost("jobs/{id:guid}/undo")]
    public async Task<ActionResult<ConsolidationApplyOutcome>> Undo(
        Guid id, [FromBody] RevisionActorRequest? request, CancellationToken ct)
        => Ok(await _consolidation.UndoAsync(id, request?.Actor, ct));

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<RevisionRun>>> Runs(
        [FromQuery] int limit = 20, CancellationToken ct = default)
        => Ok(await _revision.ListRunsAsync(limit, ct));

    [HttpGet("status")]
    public async Task<ActionResult<object>> Status(CancellationToken ct)
    {
        var lastRun = await _revision.GetLatestRunAsync(ct);
        var ready = await _consolidation.CountAsync(
            new ConsolidationJobQuery(Status: ConsolidationJobStatus.ReadyForAnalysis), ct);
        var awaiting = await _consolidation.CountAsync(
            new ConsolidationJobQuery(Status: ConsolidationJobStatus.AwaitingLlm), ct);
        var review = await _consolidation.CountAsync(
            new ConsolidationJobQuery(Status: ConsolidationJobStatus.ReadyForReview), ct);
        var errors = await _consolidation.CountAsync(
            new ConsolidationJobQuery(Status: ConsolidationJobStatus.Error), ct);
        return Ok(new
        {
            llm = new
            {
                configured = _consolidation.LlmConfigured,
                status = _consolidation.LlmConfigured ? "configured" : "not_configured"
            },
            lastRun,
            queue = new { readyForAnalysis = ready, awaitingLlm = awaiting, readyForReview = review, errors }
        });
    }
}

public sealed record RunRevisionRequest(Guid? ProjectId = null, Guid? RepositoryId = null, bool DryRun = false);

public sealed record RevisionActorRequest(string? Actor = null, string? Reason = null);

public sealed record AcceptConsolidationRequest(int ExpectedProposalVersion, string? Actor = null);
