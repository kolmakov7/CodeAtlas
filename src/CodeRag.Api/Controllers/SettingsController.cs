using CodeRag.Api.Models;
using CodeRag.Api.Services;
using CodeRag.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly ILogger<SettingsController> _logger;
    private readonly IConfiguration _config;
    private readonly SettingsTableConfigurationProvider _configProvider;

    public SettingsController(
        SettingsService settings,
        ILogger<SettingsController> logger,
        IConfiguration config,
        SettingsTableConfigurationProvider configProvider)
    {
        _settings = settings;
        _logger = logger;
        _config = config;
        _configProvider = configProvider;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var values = await _settings.GetAllAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("GET /api/settings count={Count}", values.Count);
        return Ok(AppSettingsDto.FromConfiguration(_config, values));
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] AppSettingsDto? body, CancellationToken ct)
    {
        if (body is null) return Problem(title: "Body required", statusCode: 400);
        var flat = AppSettingsDto.Flatten(body);
        _logger.LogInformation("PUT /api/settings keys={Keys}", string.Join(",", flat.Keys));
        var updated = await _settings.UpsertAsync(flat, ct).ConfigureAwait(false);
        _configProvider.FireReload();
        return Ok(AppSettingsDto.FromConfiguration(_config, updated));
    }
}
