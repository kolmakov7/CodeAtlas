using CodeRag.Api.Models;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/repository-links")]
public sealed class RepositoryLinksController : ControllerBase
{
    private static readonly string[] AllowedTypes = { "depends_on", "calls", "references", "related" };

    private readonly IRepositoryLinkStore _store;
    private readonly IRepositoryStore _repos;

    public RepositoryLinksController(IRepositoryLinkStore store, IRepositoryStore repos)
    {
        _store = store;
        _repos = repos;
    }

    /// <summary>List links, optionally filtered to those touching a repository.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? repositoryId, CancellationToken ct)
    {
        var links = repositoryId.HasValue
            ? await _store.GetByRepositoryAsync(repositoryId.Value, ct).ConfigureAwait(false)
            : await _store.ListAsync(ct).ConfigureAwait(false);
        return Ok(await MapAsync(links, ct).ConfigureAwait(false));
    }

    /// <summary>Declare a link between two repositories (human-in-the-loop for cross-repo relations).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryLinkRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "Body required", statusCode: 400);
        var type = (req.LinkType ?? "").Trim().ToLowerInvariant();
        if (!AllowedTypes.Contains(type))
            return Problem(title: $"linkType must be one of: {string.Join(", ", AllowedTypes)}", statusCode: 400);
        if (req.SourceRepositoryId == req.TargetRepositoryId)
            return Problem(title: "A repository cannot link to itself", statusCode: 400);
        if (await _repos.GetByIdAsync(req.SourceRepositoryId, ct).ConfigureAwait(false) is null)
            return Problem(title: $"Source repository {req.SourceRepositoryId} not found", statusCode: 404);
        if (await _repos.GetByIdAsync(req.TargetRepositoryId, ct).ConfigureAwait(false) is null)
            return Problem(title: $"Target repository {req.TargetRepositoryId} not found", statusCode: 404);

        RepositoryLink link;
        try
        {
            link = RepositoryLink.Create(Guid.NewGuid(), req.SourceRepositoryId, req.TargetRepositoryId, type, note: req.Note);
        }
        catch (Exception ex)
        {
            return Problem(title: ex.Message, statusCode: 400);
        }

        await _store.UpsertAsync(link, ct).ConfigureAwait(false);
        var mapped = (await MapAsync(new[] { link }, ct).ConfigureAwait(false))[0];
        return Ok(mapped);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await _store.GetByIdAsync(id, ct).ConfigureAwait(false) is null)
            return Problem(title: $"Link {id} not found", statusCode: 404);
        await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<RepositoryLinkDto[]> MapAsync(IReadOnlyList<RepositoryLink> links, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        async Task<string> NameOf(Guid id)
        {
            if (names.TryGetValue(id, out var n)) return n;
            var repo = await _repos.GetByIdAsync(id, ct).ConfigureAwait(false);
            names[id] = repo?.Name ?? id.ToString();
            return names[id];
        }

        var result = new List<RepositoryLinkDto>(links.Count);
        foreach (var l in links)
            result.Add(new RepositoryLinkDto(
                l.Id, l.SourceRepositoryId, await NameOf(l.SourceRepositoryId),
                l.TargetRepositoryId, await NameOf(l.TargetRepositoryId),
                l.LinkType, l.Confidence, l.Note, l.CreatedAt.ToString("o")));
        return result.ToArray();
    }
}
