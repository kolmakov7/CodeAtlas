using CodeRag.Api.Models;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Ports;
using CodeRag.Git.Helpers;
using CodeRag.Git.Services;
using CodeRag.Indexer;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/repositories")]
public sealed class RepositoriesController : ControllerBase
{
    private readonly IRepositoryService _repoService;
    private readonly IRepositoryStore _repoStore;
    private readonly IJobRunner _jobRunner;
    private readonly ILogger<RepositoriesController> _logger;
    private readonly string _reposRoot;

    public RepositoriesController(IRepositoryService repoService, IRepositoryStore repoStore, IJobRunner jobRunner, ILogger<RepositoriesController> logger, CodeRag.Infrastructure.Persistence.SqliteConnectionFactory factory)
    {
        _repoService = repoService;
        _repoStore = repoStore;
        _jobRunner = jobRunner;
        _logger = logger;
        var dataDir = Path.GetDirectoryName(factory.DbPath) ?? "/data";
        _reposRoot = Path.Combine(dataDir, "repos");
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "Body required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.Name)) return Problem(title: "Name required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.GitUrl)) return Problem(title: "GitUrl required", statusCode: 400);

        // Check uniqueness conflict
        var existing = await _repoStore.GetByNameAsync(req.Name, ct).ConfigureAwait(false);
        if (existing is not null) return Problem(title: $"Repository name '{req.Name}' already exists", statusCode: 409);

        var localPath = req.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            // Auto-generate inside {dataDir}/repos/<slug> (supports debug /tmp/debug-data2)
            var slug = ToSlug(req.Name);
            localPath = Path.Combine(_reposRoot, slug);
        }

try
            {
                _logger.LogInformation("POST /api/repositories name='{Name}' url='{Url}'", req.Name, MaskUrl(req.GitUrl));
                var repo = await _repoService.AddAsync(req.Name, req.GitUrl, req.Branch ?? "main", localPath, req.ProxyId, req.CredentialId, req.SyncIntervalSeconds, ct).ConfigureAwait(false);
                
                // Enqueue initial sync job to start indexing immediately
                var job = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Sync);
                await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
                
                return CreatedAtAction(nameof(GetById), new { id = repo.Id }, Map(repo));
            }
        catch (DomainValidationException ex) { return Problem(title: ex.Message, statusCode: 422); }
        catch (ArgumentException ex) { return Problem(title: ex.Message, statusCode: 400); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 422); }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var repos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
        return Ok(repos.Select(Map).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        return Ok(Map(repo));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRepositoryRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "Body required", statusCode: 400);
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);

        // Check name conflict if renaming
        if (!string.IsNullOrWhiteSpace(req.Name) && !string.Equals(req.Name.Trim(), repo.Name, StringComparison.Ordinal))
        {
            var byName = await _repoStore.GetByNameAsync(req.Name.Trim(), ct).ConfigureAwait(false);
            if (byName is not null && byName.Id != id) return Problem(title: $"Repository name '{req.Name}' already exists", statusCode: 409);
        }

        try
        {
            _logger.LogInformation("PUT /api/repositories/{Id} name='{Name}'", id, req.Name ?? "(unchanged)");
            var updated = await _repoService.UpdateAsync(id, req.Name, req.GitUrl, req.Branch, req.LocalPath, req.ProxyId, req.CredentialId, req.SyncIntervalSeconds, ct).ConfigureAwait(false);

            // Handle enabled toggle
            if (req.Enabled.HasValue && req.Enabled.Value != updated.Enabled)
            {
                updated.SetEnabled(req.Enabled.Value);
                await _repoStore.UpsertAsync(updated, ct).ConfigureAwait(false);
            }

            return Ok(Map(updated));
        }
        catch (DomainValidationException ex) { return Problem(title: ex.Message, statusCode: 422); }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(title: ex.Message, statusCode: 400); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool deleteLocalFiles = false, [FromQuery] bool deleteCheckout = false, CancellationToken ct = default)
    {
        // The Web UI sends ?deleteCheckout=true; accept both so the local checkout is actually removed.
        var deleteLocal = deleteLocalFiles || deleteCheckout;
        _logger.LogInformation("DELETE /api/repositories/{Id} deleteLocal={Flag}", id, deleteLocal);
        await _repoService.DeleteAsync(id, deleteLocal, ct).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{id:guid}/sync")]
    public async Task<IActionResult> Sync(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        if (repo.Status == RepositoryStatus.Paused) return Problem(title: "Repository is paused", statusCode: 409);

        try
        {
            _logger.LogInformation("POST /api/repositories/{Id}/sync", id);
            await _repoService.SyncAsync(id, ct).ConfigureAwait(false);
            // Also enqueue Index job for sync? The indexer Service will handle polling but we also enqueue job for immediate indexing
            var job = IndexingJob.Create(Guid.NewGuid(), id, JobType.Sync);
            await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
            return Accepted(new { jobId = job.Id });
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 422); }
    }

    [HttpPost("{id:guid}/index")]
    public async Task<IActionResult> Index(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);

        _logger.LogInformation("POST /api/repositories/{Id}/index", id);
        var job = IndexingJob.Create(Guid.NewGuid(), id, JobType.Index);
        await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
        return Accepted(new { jobId = job.Id });
    }

    [HttpPost("{id:guid}/reindex")]
    public async Task<IActionResult> Reindex(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);

        _logger.LogInformation("POST /api/repositories/{Id}/reindex", id);
        var job = IndexingJob.Create(Guid.NewGuid(), id, JobType.Rebuild);
        await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
        return Accepted(new { jobId = job.Id });
    }

    [HttpPost("{id:guid}/reembed")]
    public async Task<IActionResult> Reembed(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);

        _logger.LogInformation("POST /api/repositories/{Id}/reembed", id);
        var job = IndexingJob.Create(Guid.NewGuid(), id, JobType.Embedding);
        await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
        return Accepted(new { jobId = job.Id });
    }

    [HttpPost("rebuild-all")]
    public async Task<IActionResult> RebuildAll(CancellationToken ct)
    {
        var repos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
        var jobIds = new List<Guid>(repos.Count);
        foreach (var repo in repos)
        {
            var job = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Rebuild);
            await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
            jobIds.Add(job.Id);
        }
        _logger.LogInformation("POST /api/repositories/rebuild-all count={Count}", jobIds.Count);
        return Accepted(new { jobIds });
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        _logger.LogInformation("POST /api/repositories/{Id}/pause", id);
        await _repoService.PauseAsync(id, ct).ConfigureAwait(false);
        return Ok(new { status = "Paused" });
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        _logger.LogInformation("POST /api/repositories/{Id}/resume", id);
        await _repoService.ResumeAsync(id, ct).ConfigureAwait(false);
        return Ok(new { status = "Active" });
    }

    private static RepositoryDto Map(Repository r) => new(
        r.Id, r.Name, r.GitUrl.Value, r.Branch, r.LocalPath.Value,
        r.CurrentCommit, r.IndexedCommit, r.Status.ToString(), r.Enabled,
        r.ProxyId, r.CredentialId, r.SyncIntervalSeconds,
        r.LastSyncAt?.ToString("o"), r.LastIndexAt?.ToString("o"), r.LastError);

    private static string ToSlug(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = Guid.NewGuid().ToString("N").Substring(0, 8);
        return slug;
    }

    private static string MaskUrl(string url)
    {
        try { return SecretMasker.Mask(url); } catch { return "***"; }
    }
}
