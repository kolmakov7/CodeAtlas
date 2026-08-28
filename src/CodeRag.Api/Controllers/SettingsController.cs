using CodeRag.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(SettingsService settings, ILogger<SettingsController> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var values = await _settings.GetAllAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("GET /api/settings count={Count}", values.Count);
        return Ok(new { values });
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] Dictionary<string, string> body, CancellationToken ct)
    {
        if (body is null) return Problem(title: "Body required", statusCode: 400);
        _logger.LogInformation("PUT /api/settings keys={Keys}", string.Join(",", body.Keys));
        var updated = await _settings.UpsertAsync(body, ct).ConfigureAwait(false);
        return Ok(new { values = updated });
    }
}
