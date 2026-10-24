using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Knowledge.Models;
using CodeRag.Knowledge.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly KnowledgeService _knowledge;

    public KnowledgeController(KnowledgeService knowledge) => _knowledge = knowledge;

    [HttpPost]
    public async Task<ActionResult<object>> Save(
        [FromBody] SaveKnowledgeRequest request, CancellationToken ct)
    {
        var (entry, created) = await _knowledge.SaveAsync(request, ct);
        if (!created) return Ok(new { entry, created });
        return CreatedAtAction(nameof(Get), new { id = entry.Id }, new { entry, created });
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<KnowledgeEntry>>> List(
        [FromQuery] string? query,
        [FromQuery] KnowledgeType[]? types,
        [FromQuery] KnowledgeStatus[]? statuses,
        [FromQuery] Guid? projectId,
        [FromQuery] Guid? repositoryId,
        [FromQuery] bool globalOnly = false,
        [FromQuery] bool includeArchived = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
        => Ok(await _knowledge.ListAsync(new KnowledgeQuery(
            query, types, statuses, projectId, repositoryId, globalOnly, includeArchived, limit, offset), ct));

    [HttpPost("search")]
    public async Task<ActionResult<KnowledgeSearchResult>> Search(
        [FromBody] KnowledgeSearchRequest request, CancellationToken ct)
        => Ok(await _knowledge.SearchAsync(request, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnowledgeDetail>> Get(
        Guid id, [FromQuery] bool includeLinks = true, [FromQuery] bool includeHistory = true,
        CancellationToken ct = default)
        => Ok(await _knowledge.GetDetailAsync(id, includeLinks, includeHistory, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<KnowledgeEntry>> Update(
        Guid id, [FromBody] UpdateKnowledgeRequest request, CancellationToken ct)
        => Ok(await _knowledge.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/stale")]
    public async Task<ActionResult<KnowledgeEntry>> MarkStale(
        Guid id, [FromBody] MarkKnowledgeStaleRequest request, CancellationToken ct)
        => Ok(await _knowledge.MarkStaleAsync(id, request.ExpectedVersion, request.Reason, request.Actor, ct));

    [HttpPost("{id:guid}/links")]
    public async Task<ActionResult<KnowledgeLinkResult>> Link(
        Guid id, [FromBody] KnowledgeLinkRequest request, [FromQuery] string? actor,
        CancellationToken ct)
        => Ok(await _knowledge.LinkAsync(id, request, actor, ct));

    [HttpDelete("{id:guid}/links")]
    public async Task<IActionResult> Unlink(
        Guid id, [FromQuery] string targetType, [FromQuery] Guid targetId, [FromQuery] string relation,
        [FromQuery] string? actor, CancellationToken ct)
        => await _knowledge.UnlinkAsync(id, targetType, targetId, relation, actor, ct)
            ? NoContent()
            : NotFound();

    [HttpGet("{id:guid}/related")]
    public async Task<ActionResult<IReadOnlyList<KnowledgeEntry>>> Related(
        Guid id, [FromQuery] int depth = 1, [FromQuery] string? relation = null,
        CancellationToken ct = default)
        => Ok(await _knowledge.GetRelatedAsync(id, depth, relation, ct));

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<EntityHistoryEntry>>> History(
        Guid id, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await _knowledge.GetHistoryAsync(id, limit, ct));

    [HttpPost("reembed")]
    public async Task<ActionResult<object>> Reembed([FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(new { embedded = await _knowledge.ReembedAsync(limit, ct) });
}

public sealed record MarkKnowledgeStaleRequest(int ExpectedVersion, string? Reason = null, string? Actor = null);
