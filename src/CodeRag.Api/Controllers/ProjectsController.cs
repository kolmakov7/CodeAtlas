using CodeRag.Domain.Entities;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly ProjectService _projects;

    public ProjectsController(ProjectService projects) => _projects = projects;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Project>>> List(CancellationToken ct)
        => Ok(await _projects.ListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Project>> Get(Guid id, CancellationToken ct)
    {
        var project = await _projects.GetAsync(id, ct);
        return Ok(project);
    }

    [HttpGet("by-repository/{repositoryId:guid}")]
    public async Task<ActionResult<IReadOnlyList<Project>>> GetByRepository(Guid repositoryId, CancellationToken ct)
        => Ok(await _projects.GetByRepositoryAsync(repositoryId, ct));

    [HttpPost]
    public async Task<ActionResult<Project>> Create([FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        var project = await _projects.CreateAsync(
            request.Name, request.RepositoryIds ?? Array.Empty<Guid>(), request.Path, request.Language, ct);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, project);
    }

    [HttpPut("{id:guid}/repositories")]
    public async Task<IActionResult> SetRepositories(
        Guid id, [FromBody] SetProjectRepositoriesRequest request, CancellationToken ct)
    {
        await _projects.SetRepositoriesAsync(id, request.RepositoryIds, ct);
        return NoContent();
    }
}

public sealed record CreateProjectRequest(
    string? Name,
    IReadOnlyList<Guid>? RepositoryIds = null,
    string? Path = null,
    string? Language = null);

public sealed record SetProjectRepositoriesRequest(IReadOnlyList<Guid> RepositoryIds);
