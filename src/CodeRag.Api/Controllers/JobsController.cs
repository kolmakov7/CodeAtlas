using CodeRag.Api.Models;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Indexer;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public sealed class JobsController : ControllerBase
{
    private readonly IIndexingJobStore _store;
    private readonly IJobRunner _runner;
    private readonly ILogger<JobsController> _logger;

    public JobsController(IIndexingJobStore store, IJobRunner runner, ILogger<JobsController> logger)
    {
        _store = store;
        _runner = runner;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? repositoryId, [FromQuery] string? status, CancellationToken ct)
    {
        _logger.LogInformation("GET /api/jobs repo={Repo} status={Status}", repositoryId, status);
        var jobs = await _store.ListAsync(repositoryId, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<JobStatus>(status, true, out var st))
            jobs = jobs.Where(j => j.Status == st).ToList();

        return Ok(jobs.Select(j => new JobDto(
            j.Id, j.RepositoryId, j.Type.ToString(), j.Status.ToString(),
            j.StartedAt.ToString("o"), j.FinishedAt?.ToString("o"), j.Progress, j.Error, j.FromCommit, j.ToCommit)).ToList());
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var job = await _store.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (job is null) return Problem(title: $"Job {id} not found", statusCode: 404);
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            return Problem(title: $"Job {id} already {job.Status}", statusCode: 409);

        _logger.LogInformation("POST /api/jobs/{Id}/cancel", id);
        await _runner.CancelAsync(id, ct).ConfigureAwait(false);
        return Ok(new { status = "Cancelled" });
    }
}
