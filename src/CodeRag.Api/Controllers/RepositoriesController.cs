using CodeRag.Api.Models;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using CodeRag.Domain.Ports;
using CodeRag.Git.Helpers;
using CodeRag.Git.Services;
using CodeRag.Git.Stores;
using CodeRag.Indexer;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/repositories")]
public sealed class RepositoriesController : ControllerBase
{
    private readonly IRepositoryService _repoService;
    private readonly IRepositoryStore _repoStore;
    private readonly IRepositoryGroupStore _groupStore;
    private readonly IJobRunner _jobRunner;
    private readonly ILogger<RepositoriesController> _logger;
    private readonly string _reposRoot;

    public RepositoriesController(IRepositoryService repoService, IRepositoryStore repoStore, IRepositoryGroupStore groupStore, IJobRunner jobRunner, ILogger<RepositoriesController> logger, CodeRag.Infrastructure.Persistence.SqliteConnectionFactory factory)
    {
        _repoService = repoService;
        _repoStore = repoStore;
        _groupStore = groupStore;
        _jobRunner = jobRunner;
        _logger = logger;
        var dataDir = Path.GetDirectoryName(factory.DbPath) ?? "/data";
        _reposRoot = Path.Combine(dataDir, "repos");
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateRepositoryRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "Body required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.GitUrl)) return Problem(title: "GitUrl required", statusCode: 400);
        var gid = req.GroupId ?? req.ProxyId ?? req.CredentialId;
        if (!gid.HasValue && (req.ProxyId.HasValue || req.CredentialId.HasValue))
        {
            var def = await _groupStore.GetByNameAsync("Default", ct).ConfigureAwait(false);
            gid = def?.Id;
        }
        else if (!gid.HasValue)
        {
            gid = req.GroupId;
        }
        // If still null, use passed GroupId directly
        if (!gid.HasValue) gid = req.GroupId;
        var (available, error) = await _repoService.ValidateAsync(req.GitUrl, gid, ct).ConfigureAwait(false);
        if (!available)
            return Ok(new ValidateRepositoryResponse(false, error));
        return Ok(new ValidateRepositoryResponse(true, null));
    }

    [HttpGet("branches")]
    public async Task<IActionResult> GetBranches([FromQuery] string gitUrl, [FromQuery] Guid? groupId, [FromQuery] Guid? proxyId, [FromQuery] Guid? credentialId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gitUrl)) return Problem(title: "gitUrl query required", statusCode: 400);
        var gid = groupId ?? proxyId ?? credentialId;
        if (!gid.HasValue && (proxyId.HasValue || credentialId.HasValue))
        {
            var def = await _groupStore.GetByNameAsync("Default", ct).ConfigureAwait(false);
            gid = def?.Id;
        }
        if (!gid.HasValue) gid = groupId;
        try
        {
            var branches = await _repoService.GetBranchesAsync(gitUrl, gid, ct).ConfigureAwait(false);
            return Ok(new BranchesResponse(branches));
        }
        catch (Exception ex)
        {
            return Problem(title: SecretMasker.Mask(ex.Message), statusCode: 422);
        }
    }

    [HttpGet("root-directories")]
    public async Task<IActionResult> GetRootDirectories([FromQuery] string gitUrl, [FromQuery] string branch, [FromQuery] Guid? groupId, [FromQuery] Guid? proxyId, [FromQuery] Guid? credentialId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gitUrl)) return Problem(title: "gitUrl required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(branch)) return Problem(title: "branch required", statusCode: 400);
        var gid = groupId ?? proxyId ?? credentialId;
        if (!gid.HasValue && (proxyId.HasValue || credentialId.HasValue))
        {
            var def = await _groupStore.GetByNameAsync("Default", ct).ConfigureAwait(false);
            gid = def?.Id;
        }
        if (!gid.HasValue) gid = groupId;
        try
        {
            var dirs = await _repoService.GetRootDirectoriesAsync(gitUrl, branch, gid, ct).ConfigureAwait(false);
            return Ok(new RootDirectoriesResponse(dirs));
        }
        catch (Exception ex)
        {
            return Problem(title: SecretMasker.Mask(ex.Message), statusCode: 422);
        }
    }

    [HttpGet("{id:guid}/branches")]
    public async Task<IActionResult> GetBranchesForRepo(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        try
        {
            var branches = await _repoService.GetBranchesAsync(repo.GitUrl.Value, repo.GroupId, ct).ConfigureAwait(false);
            return Ok(new BranchesResponse(branches));
        }
        catch (Exception ex)
        {
            return Problem(title: SecretMasker.Mask(ex.Message), statusCode: 422);
        }
    }

    [HttpGet("{id:guid}/root-directories")]
    public async Task<IActionResult> GetRootDirectoriesForRepo(Guid id, [FromQuery] string? branch, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        try
        {
            var dirs = await _repoService.GetRootDirectoriesForRepositoryAsync(id, branch, ct).ConfigureAwait(false);
            return Ok(new RootDirectoriesResponse(dirs));
        }
        catch (Exception ex)
        {
            return Problem(title: SecretMasker.Mask(ex.Message), statusCode: 422);
        }
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
                _logger.LogInformation("POST /api/repositories name='{Name}' url='{Url}' branch='{Branch}' filter={Filter}", req.Name, MaskUrl(req.GitUrl), req.Branch, req.DirectoryFilterEnabled);
                var gid = req.GroupId ?? req.ProxyId ?? req.CredentialId; // fallback for legacy clients
                // If legacy proxy/cred supplied without group, try to resolve group that matches; otherwise use groupId directly
                Guid? effectiveGroup = req.GroupId;
                if (!effectiveGroup.HasValue && (req.ProxyId.HasValue || req.CredentialId.HasValue))
                {
                    // Try to find or create implicit group? For now use Default
                    var def = await _groupStore.GetByNameAsync("Default", ct).ConfigureAwait(false);
                    effectiveGroup = def?.Id;
                }
                var repo = await _repoService.AddAsync(req.Name, req.GitUrl, req.Branch ?? "main", localPath, effectiveGroup, req.SyncIntervalSeconds, req.DirectoryFilterEnabled, req.AllowedDirectories, ct).ConfigureAwait(false);
                
                // If repo creation resulted in Error status (e.g., ls-remote failed), don't enqueue jobs and return error
                if (repo.Status == RepositoryStatus.Error)
                {
                    return Problem(title: repo.LastError ?? "Repository validation failed", statusCode: 422);
                }

                // Clone — отдельный уникальный таск сразу после добавления, Sync/Index после
                var job = IndexingJob.Create(Guid.NewGuid(), repo.Id, JobType.Clone);
                await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
                
                return CreatedAtAction(nameof(GetById), new { id = repo.Id }, await MapAsync(repo, ct).ConfigureAwait(false));
            }
        catch (DomainValidationException ex) { return Problem(title: ex.Message, statusCode: 422); }
        catch (ArgumentException ex) { return Problem(title: ex.Message, statusCode: 400); }
        catch (InvalidOperationException ex) { return Problem(title: ex.Message, statusCode: 422); }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var repos = await _repoStore.ListAsync(ct).ConfigureAwait(false);
        var mapped = new List<RepositoryDto>();
        foreach (var r in repos) mapped.Add(await MapAsync(r, ct).ConfigureAwait(false));
        return Ok(mapped);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        return Ok(await MapAsync(repo, ct).ConfigureAwait(false));
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

        // Capture before state for change detection
        var beforeBranch = repo.Branch;
        var beforeFilterEnabled = repo.DirectoryFilterEnabled;
        var beforeDirs = repo.AllowedDirectories.ToArray();

        try
        {
            _logger.LogInformation("PUT /api/repositories/{Id} name='{Name}' branch='{Branch}' filter={Filter}", id, req.Name ?? "(unchanged)", req.Branch ?? "(unchanged)", req.DirectoryFilterEnabled);
            var gid = req.GroupId ?? req.ProxyId; // legacy fallback
            var updated = await _repoService.UpdateAsync(id, req.Name, req.GitUrl, req.Branch, req.LocalPath, gid, req.SyncIntervalSeconds, req.DirectoryFilterEnabled, req.AllowedDirectories, ct).ConfigureAwait(false);

            // Handle enabled toggle
            if (req.Enabled.HasValue && req.Enabled.Value != updated.Enabled)
            {
                updated.SetEnabled(req.Enabled.Value);
                await _repoStore.UpsertAsync(updated, ct).ConfigureAwait(false);
            }

            // Detect branch/filter change and trigger reindex if needed
            var branchChanged = !string.Equals(beforeBranch, updated.Branch, StringComparison.Ordinal);
            var filterChanged = beforeFilterEnabled != updated.DirectoryFilterEnabled || !beforeDirs.SequenceEqual(updated.AllowedDirectories);
            if (branchChanged || filterChanged)
            {
                _logger.LogInformation("Repository {Id} branch/filter changed branchChanged={Bc} filterChanged={Fc}, enqueue Rebuild", id, branchChanged, filterChanged);
                var job = IndexingJob.Create(Guid.NewGuid(), updated.Id, JobType.Rebuild);
                await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
            }

            return Ok(await MapAsync(updated, ct).ConfigureAwait(false));
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

    [HttpPost("{id:guid}/clone")]
    public async Task<IActionResult> Clone(Guid id, CancellationToken ct)
    {
        var repo = await _repoStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {id} not found", statusCode: 404);
        if (repo.Status == RepositoryStatus.Paused) return Problem(title: "Repository is paused", statusCode: 409);
        _logger.LogInformation("POST /api/repositories/{Id}/clone", id);
        var job = IndexingJob.Create(Guid.NewGuid(), id, JobType.Clone);
        await _jobRunner.EnqueueAsync(job, ct).ConfigureAwait(false);
        return Accepted(new { jobId = job.Id });
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
            // Sync теперь не блокирует clone — Clone отдельный таск, Sync только fetch
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

    private async Task<RepositoryDto> MapAsync(Repository r, CancellationToken ct)
    {
        Guid? effProxy = null, effCred = null;
        if (r.GroupId.HasValue)
        {
            var g = await _groupStore.GetByIdAsync(r.GroupId.Value, ct).ConfigureAwait(false);
            if (g is not null) { effProxy = g.ProxyId; effCred = g.CredentialId; }
        }
        else
        {
            effProxy = r.ProxyId;
            effCred = r.CredentialId;
        }
        return new RepositoryDto(
            r.Id, r.Name, r.GitUrl.Value, r.Branch, r.LocalPath.Value,
            r.CurrentCommit, r.IndexedCommit, r.Status.ToString(), r.Enabled,
            r.GroupId, effProxy, effCred, r.SyncIntervalSeconds,
            r.LastSyncAt?.ToString("o"), r.LastIndexAt?.ToString("o"), r.LastError,
            r.DirectoryFilterEnabled, r.AllowedDirectories.ToArray());
    }

    private static RepositoryDto Map(Repository r) => new(
        r.Id, r.Name, r.GitUrl.Value, r.Branch, r.LocalPath.Value,
        r.CurrentCommit, r.IndexedCommit, r.Status.ToString(), r.Enabled,
        r.GroupId, r.ProxyId, r.CredentialId, r.SyncIntervalSeconds,
        r.LastSyncAt?.ToString("o"), r.LastIndexAt?.ToString("o"), r.LastError,
        r.DirectoryFilterEnabled, r.AllowedDirectories.ToArray());

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
