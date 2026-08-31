using CodeRag.Application.Ports;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/activity")]
public sealed class ActivityController : ControllerBase
{
    private readonly IActivityQueryHandler _handler;
    public ActivityController(IActivityQueryHandler handler) => _handler = handler;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var rows = await _handler.HandleAsync(limit, ct).ConfigureAwait(false);
        return Ok(rows.Select(r => new { id = r.Id, at = r.At, actor = r.Actor, action = r.Action, repositoryId = r.RepositoryId, repositoryName = (string?)null, detail = r.Detail }));
    }
}
