using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Git.Stores;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/credentials")]
public sealed class CredentialsController : ControllerBase
{
    private readonly ICredentialStore _store;
    private readonly ILogger<CredentialsController> _logger;
    public CredentialsController(ICredentialStore store, ILogger<CredentialsController> logger) { _store = store; _logger = logger; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await _store.ListAsync(ct).ConfigureAwait(false);
        var dto = list.Select(c => new { id = c.Id, kind = c.Kind.ToString(), username = c.Username });
        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCredentialRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Kind)) return BadRequest("Kind required");
        var kind = Enum.TryParse<CredentialKind>(req.Kind, true, out var k) ? k : CredentialKind.HttpsToken;
        var cred = Credential.Create(Guid.NewGuid(), kind, req.Username, req.Secret);
        await _store.UpsertAsync(cred, ct).ConfigureAwait(false);
        _logger.LogInformation("POST /api/credentials created {Id} {Kind}", cred.Id, cred.Kind);
        return CreatedAtAction(nameof(Get), new { id = cred.Id }, new { id = cred.Id, kind = cred.Kind.ToString(), username = cred.Username });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await _store.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (c is null) return NotFound();
        return Ok(new { id = c.Id, kind = c.Kind.ToString(), username = c.Username });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }
}

public sealed record CreateCredentialRequest(string Kind, string? Username, string? Secret);
