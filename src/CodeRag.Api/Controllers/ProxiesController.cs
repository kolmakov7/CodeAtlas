using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Git.Stores;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/proxies")]
public sealed class ProxiesController : ControllerBase
{
    private readonly IProxyStore _store;
    private readonly IRepositoryGroupStore _groupStore;
    private readonly ILogger<ProxiesController> _logger;
    public ProxiesController(IProxyStore store, IRepositoryGroupStore groupStore, ILogger<ProxiesController> logger) { _store = store; _groupStore = groupStore; _logger = logger; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await _store.ListAsync(ct).ConfigureAwait(false);
        var dto = list.Select(p => new { id = p.Id, name = p.Name, scheme = p.Scheme.ToString().ToLower(), host = p.Host, port = p.Port, username = p.Username });
        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProxyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Host)) return BadRequest("Name and Host required");
        var scheme = Enum.TryParse<ProxyScheme>(req.Scheme, true, out var s) ? s : ProxyScheme.Http;
        try
        {
            var proxy = ProxyConfig.Create(Guid.NewGuid(), req.Name, scheme, req.Host, req.Port, req.Username, req.Password);
            await _store.UpsertAsync(proxy, ct).ConfigureAwait(false);
            _logger.LogInformation("POST /api/proxies created {Id} {Name}", proxy.Id, proxy.Name);
            return CreatedAtAction(nameof(Get), new { id = proxy.Id }, new { id = proxy.Id, name = proxy.Name, scheme = proxy.Scheme.ToString().ToLower(), host = proxy.Host, port = proxy.Port, username = proxy.Username });
        }
        catch (CodeRag.Domain.Exceptions.DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var p = await _store.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (p is null) return NotFound();
        return Ok(new { id = p.Id, name = p.Name, scheme = p.Scheme.ToString().ToLower(), host = p.Host, port = p.Port, username = p.Username });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var groups = await _groupStore.GetByProxyIdAsync(id, ct).ConfigureAwait(false);
        if (groups.Count > 0)
        {
            var names = string.Join(", ", groups.Select(g => $"'{g.Name}'"));
            return Problem(title: $"Proxy is used by group(s): {names}. Remove it from groups before deleting.", statusCode: 409);
        }
        await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var p = await _store.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (p is null) return NotFound();
        // Simple test: try to resolve host
        try { var ips = await System.Net.Dns.GetHostAddressesAsync(p.Host, ct).ConfigureAwait(false); return Ok(new { ok = true, ips = ips.Select(ip => ip.ToString()) }); }
        catch (Exception ex) { return Ok(new { ok = false, error = ex.Message }); }
    }
}

public sealed record CreateProxyRequest(string Name, string Scheme, string Host, int Port, string? Username, string? Password);
