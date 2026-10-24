using CodeRag.Domain.Ports;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class UsageController : ControllerBase
{
    private readonly IUsageQueryService _usage;
    private readonly IRepositoryStore _repos;

    public UsageController(IUsageQueryService usage, IRepositoryStore repos)
    {
        _usage = usage;
        _repos = repos;
    }

    /// <summary>Top-used objects within a repository (drives documentation prioritisation).</summary>
    [HttpGet("repositories/{repoId:guid}/usage")]
    public async Task<IActionResult> GetForRepository(Guid repoId, [FromQuery] int limit = 20, [FromQuery] int days = 30, CancellationToken ct = default)
    {
        var repo = await _repos.GetByIdAsync(repoId, ct).ConfigureAwait(false);
        if (repo is null) return Problem(title: $"Repository {repoId} not found", statusCode: 404);
        var rows = await _usage.GetTopAsync(repoId, limit, days, ct).ConfigureAwait(false);
        return Ok(rows);
    }

    /// <summary>Top-used objects across all repositories.</summary>
    [HttpGet("usage/top")]
    public async Task<IActionResult> GetTop([FromQuery] int limit = 20, [FromQuery] int days = 30, CancellationToken ct = default)
        => Ok(await _usage.GetTopAsync(null, limit, days, ct).ConfigureAwait(false));
}
