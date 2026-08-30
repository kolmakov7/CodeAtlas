using CodeRag.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly HealthService _health;
    private readonly StatisticsService _stats;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(HealthService health, StatisticsService stats, ILogger<DiagnosticsController> logger)
    {
        _health = health;
        _stats = stats;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var h = await _health.CheckDetailedAsync(ct).ConfigureAwait(false);
        // Map HealthDetailsDto -> Diagnostics shape expected by Web (types.ts)
        string MapStatus(string s) => s == "Healthy" ? "OK" : s == "Degraded" ? "FAIL" : "FAIL";
        var diag = new
        {
            database = new { status = MapStatus(h.Database.Status), detail = h.Database.Message },
            vectorIndex = new { status = MapStatus(h.Vector.Status), detail = h.Vector.Message, count = (int?)null, sizeBytes = (long?)null },
            embeddingApi = new { status = MapStatus(h.Embedding.Status), detail = h.Embedding.Message, latencyMs = h.Embedding.LatencyMs, model = (string?)null },
            indexer = new { status = MapStatus(h.Indexer.Status), detail = h.Indexer.Message, queueDepth = (int?)null },
            storage = new { status = MapStatus(h.Storage.Status), detail = h.Storage.Message, freeBytes = (long?)null }
        };
        _logger.LogInformation("GET /api/diagnostics status={Status}", h.Status);
        return Ok(diag);
    }
}
