using CodeRag.Api.Models;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using CodeRag.Git.Stores;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/repository-groups")]
public sealed class RepositoryGroupsController : ControllerBase
{
    private readonly IRepositoryGroupStore _groupStore;
    private readonly IProxyStore _proxyStore;
    private readonly ICredentialStore _credStore;
    private readonly ILogger<RepositoryGroupsController> _logger;

    public RepositoryGroupsController(IRepositoryGroupStore groupStore, IProxyStore proxyStore, ICredentialStore credStore, ILogger<RepositoryGroupsController> logger)
    {
        _groupStore = groupStore;
        _proxyStore = proxyStore;
        _credStore = credStore;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var groups = await _groupStore.ListAsync(ct).ConfigureAwait(false);
        return Ok(groups.Select(Map).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var g = await _groupStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (g is null) return NotFound();
        return Ok(Map(g));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryGroupRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name required");
        var existing = await _groupStore.GetByNameAsync(req.Name.Trim(), ct).ConfigureAwait(false);
        if (existing is not null) return Conflict($"Group name '{req.Name}' already exists");

        if (req.ProxyId.HasValue)
        {
            var p = await _proxyStore.GetByIdAsync(req.ProxyId.Value, ct).ConfigureAwait(false);
            if (p is null) return BadRequest($"Proxy {req.ProxyId} not found");
        }
        if (req.CredentialId.HasValue)
        {
            var c = await _credStore.GetByIdAsync(req.CredentialId.Value, ct).ConfigureAwait(false);
            if (c is null) return BadRequest($"Credential {req.CredentialId} not found");
        }

        var group = RepositoryGroup.Create(Guid.NewGuid(), req.Name.Trim(), req.ProxyId, req.CredentialId);
        await _groupStore.UpsertAsync(group, ct).ConfigureAwait(false);
        _logger.LogInformation("POST /api/repository-groups created {Id} {Name}", group.Id, group.Name);
        return CreatedAtAction(nameof(Get), new { id = group.Id }, Map(group));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRepositoryGroupRequest req, CancellationToken ct)
    {
        var g = await _groupStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (g is null) return NotFound();
        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name must not be empty");
            var byName = await _groupStore.GetByNameAsync(req.Name.Trim(), ct).ConfigureAwait(false);
            if (byName is not null && byName.Id != id) return Conflict($"Group name '{req.Name}' already exists");
            g.Rename(req.Name);
        }
        if (req.ProxyId is not null)
        {
            if (req.ProxyId != Guid.Empty)
            {
                var p = await _proxyStore.GetByIdAsync(req.ProxyId.Value, ct).ConfigureAwait(false);
                if (p is null) return BadRequest($"Proxy {req.ProxyId} not found");
                g.SetProxy(req.ProxyId);
            }
            else
            {
                g.SetProxy(null);
            }
        }
        if (req.CredentialId is not null)
        {
            if (req.CredentialId != Guid.Empty)
            {
                var c = await _credStore.GetByIdAsync(req.CredentialId.Value, ct).ConfigureAwait(false);
                if (c is null) return BadRequest($"Credential {req.CredentialId} not found");
                g.SetCredential(req.CredentialId);
            }
            else
            {
                g.SetCredential(null);
            }
        }
        await _groupStore.UpsertAsync(g, ct).ConfigureAwait(false);
        return Ok(Map(g));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var g = await _groupStore.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (g is null) return NoContent();
        if (string.Equals(g.Name, "Default", StringComparison.OrdinalIgnoreCase))
            return Problem(title: "Cannot delete Default group", statusCode: 400);

        var repos = await _groupStore.GetRepositoriesByGroupAsync(id, ct).ConfigureAwait(false);
        if (repos.Count > 0)
            return Problem(title: $"Cannot delete group '{g.Name}': used by {repos.Count} repository(ies)", statusCode: 409);

        await _groupStore.DeleteAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    private static RepositoryGroupDto Map(RepositoryGroup g) => new(g.Id, g.Name, g.ProxyId, g.CredentialId, g.CreatedAt.ToString("o"));
}
