using CodeRag.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly HealthService _health;
    private readonly ILogger<HealthController> _logger;

    public HealthController(HealthService health, ILogger<HealthController> logger)
    {
        _health = health;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var details = await _health.CheckDetailedAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("GET /api/health status={Status}", details.Status);
        return Ok(details);
    }
}

[ApiController]
public sealed class LiveReadyController : ControllerBase
{
    private readonly HealthService _health;

    public LiveReadyController(HealthService health) => _health = health;

    [HttpGet("/health/live")]
    public IActionResult Live() => Ok(new { status = "Healthy" });

    [HttpGet("/health/ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        var (ok, msg) = await _health.CheckReadyAsync(ct).ConfigureAwait(false);
        if (ok) return Ok(new { status = "Healthy" });
        return StatusCode(503, new { status = "Unhealthy", message = msg });
    }
}
