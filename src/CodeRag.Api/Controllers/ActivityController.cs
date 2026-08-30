using CodeRag.Infrastructure.Persistence;
using Dapper;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/activity")]
public sealed class ActivityController : ControllerBase
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<ActivityController> _logger;
    public ActivityController(SqliteConnectionFactory factory, ILogger<ActivityController> logger) { _factory = factory; _logger = logger; }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await conn.QueryAsync<ActivityRow>("SELECT id, at, actor, action, repository_id as repositoryId, detail FROM activity_log ORDER BY at DESC LIMIT @limit;", new { limit }).ConfigureAwait(false);
            return Ok(rows.Select(r => new { id = r.id, at = r.at, actor = r.actor, action = r.action, repositoryId = r.repositoryId, repositoryName = (string?)null, detail = r.detail }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/activity failed, returning empty");
            return Ok(Array.Empty<object>());
        }
    }

    private sealed class ActivityRow
    {
        public string id { get; set; } = "";
        public string at { get; set; } = "";
        public string actor { get; set; } = "";
        public string action { get; set; } = "";
        public string? repositoryId { get; set; }
        public string? detail { get; set; }
    }
}
