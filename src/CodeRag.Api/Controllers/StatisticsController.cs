using CodeRag.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class StatisticsController : ControllerBase
{
    private readonly StatisticsService _stats;
    private readonly ILogger<StatisticsController> _logger;

    public StatisticsController(StatisticsService stats, ILogger<StatisticsController> logger)
    {
        _stats = stats;
        _logger = logger;
    }

    [HttpGet("repositories/{id:guid}/statistics")]
    public async Task<IActionResult> RepoStats(Guid id, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("GET /api/repositories/{Id}/statistics", id);
            var s = await _stats.GetRepositoryStatisticsAsync(id, ct).ConfigureAwait(false);
            return Ok(s);
        }
        catch (KeyNotFoundException ex) { return Problem(title: ex.Message, statusCode: 404); }
    }

    [HttpGet("statistics")]
    public async Task<IActionResult> GlobalStats(CancellationToken ct)
    {
        _logger.LogInformation("GET /api/statistics");
        var s = await _stats.GetGlobalStatisticsAsync(ct).ConfigureAwait(false);
        return Ok(s);
    }
}
