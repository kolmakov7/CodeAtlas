using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    private readonly TaskService _tasks;

    public TasksController(TaskService tasks) => _tasks = tasks;

    [HttpPost]
    public async Task<ActionResult<TaskResult>> Create([FromBody] CreateTaskRequest request, CancellationToken ct)
    {
        var result = await _tasks.CreateAsync(request, ct);
        if (!result.Created) return Ok(result);
        return CreatedAtAction(nameof(Get), new { id = result.Task.Id }, result);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaskItem>>> List(
        [FromQuery] string? query,
        [FromQuery] TaskState? status,
        [FromQuery] TaskKind? kind,
        [FromQuery] Guid? projectId,
        [FromQuery] Guid? repositoryId,
        [FromQuery] Guid? parentTaskId,
        [FromQuery] bool includeArchived = false,
        [FromQuery] bool rootOnly = false,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
        => Ok(await _tasks.ListAsync(new TaskQuery(
            query, status, kind, projectId, repositoryId, parentTaskId,
            includeArchived, rootOnly, limit, offset), ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaskDetail>> Get(Guid id, CancellationToken ct)
        => Ok(await _tasks.GetDetailAsync(id, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TaskResult>> Update(
        Guid id, [FromBody] UpdateTaskRequest request, CancellationToken ct)
        => Ok(await _tasks.UpdateAsync(id, request, ct));

    [HttpGet("tree")]
    public async Task<ActionResult<TaskTreeResult>> Tree(
        [FromQuery] Guid? rootTaskId,
        [FromQuery] Guid? projectId,
        [FromQuery] Guid? repositoryId,
        [FromQuery] int maxDepth = 10,
        [FromQuery] bool includeArchived = false,
        CancellationToken ct = default)
        => Ok(await _tasks.GetTreeAsync(rootTaskId, projectId, repositoryId, maxDepth, includeArchived, ct));

    [HttpPost("{id:guid}/archive")]
    public async Task<ActionResult<TaskItem>> Archive(
        Guid id, [FromQuery] int expectedVersion, [FromQuery] string? actor, CancellationToken ct)
        => Ok(await _tasks.ArchiveAsync(id, expectedVersion, actor, ct));

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<TaskItem>> Restore(
        Guid id, [FromQuery] int expectedVersion, [FromQuery] string? actor, CancellationToken ct)
        => Ok(await _tasks.RestoreAsync(id, expectedVersion, actor, ct));

    [HttpPost("{id:guid}/dependencies")]
    public async Task<ActionResult<TaskDependencyResult>> AddDependency(
        Guid id, [FromBody] AddTaskDependencyRequest request, CancellationToken ct)
        => Ok(await _tasks.AddDependencyAsync(id, request.DependsOnTaskId, request.Actor, ct));

    [HttpDelete("{id:guid}/dependencies/{dependsOnTaskId:guid}")]
    public async Task<IActionResult> RemoveDependency(
        Guid id, Guid dependsOnTaskId, [FromQuery] string? actor, CancellationToken ct)
        => await _tasks.RemoveDependencyAsync(id, dependsOnTaskId, actor, ct) ? NoContent() : NotFound();

    [HttpGet("{id:guid}/dependencies")]
    public async Task<ActionResult<IReadOnlyList<EntityLink>>> GetDependencies(
        Guid id, CancellationToken ct)
        => Ok(await _tasks.GetDependenciesAsync(id, ct));

    [HttpPost("{id:guid}/comments")]
    public async Task<ActionResult<TaskCommentResult>> AddComment(
        Guid id, [FromBody] AddTaskCommentRequest request, CancellationToken ct)
        => Ok(await _tasks.AddCommentAsync(id, request.Body, request.Author, ct));

    [HttpGet("{id:guid}/comments")]
    public async Task<ActionResult<IReadOnlyList<TaskComment>>> GetComments(
        Guid id, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _tasks.GetCommentsAsync(id, limit, ct));

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<TaskHistoryResult>> GetHistory(
        Guid id, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _tasks.GetHistoryAsync(id, limit, ct));

    [HttpPost("{id:guid}/links")]
    public async Task<ActionResult<KnowledgeLinkResult>> Link(
        Guid id, [FromBody] KnowledgeLinkRequest request, [FromQuery] string? actor, CancellationToken ct)
        => Ok(await _tasks.LinkAsync(id, request, actor, ct));

    [HttpDelete("{id:guid}/links")]
    public async Task<IActionResult> Unlink(
        Guid id, [FromQuery] string targetType, [FromQuery] Guid targetId, [FromQuery] string relation,
        [FromQuery] string? actor, CancellationToken ct)
        => await _tasks.UnlinkAsync(id, targetType, targetId, relation, actor, ct) ? NoContent() : NotFound();

    [HttpPost("import")]
    public async Task<ActionResult<TodoImportResult>> Import(
        [FromBody] ImportTodosRequest request, CancellationToken ct)
        => Ok(await _tasks.ImportTodosAsync(
            request.RepositoryId, request.Path, request.ProjectId, request.DryRun,
            request.Actor, ct));

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] Guid? projectId, [FromQuery] Guid? repositoryId,
        [FromQuery] bool includeArchived = false, CancellationToken ct = default)
    {
        var result = await _tasks.ExportAsync(projectId, repositoryId, includeArchived, ct);
        return Content(result.Content, "text/markdown; charset=utf-8");
    }
}

public sealed record AddTaskDependencyRequest(Guid DependsOnTaskId, string? Actor = null);

public sealed record AddTaskCommentRequest(string Body, string? Author = null);

public sealed record ImportTodosRequest(
    Guid RepositoryId, string Path, Guid? ProjectId = null, bool DryRun = false, string? Actor = null);
