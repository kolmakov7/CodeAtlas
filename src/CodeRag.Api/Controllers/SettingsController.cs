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
        // Validate embedding endpoints: must be http:// or https:// with host (IP or DNS)
        if (body.Embeddings?.Code?.Endpoint is not null && !IsValidEndpoint(body.Embeddings.Code.Endpoint))
            return Problem(title: $"Code embedding endpoint '{body.Embeddings.Code.Endpoint}' is not valid. Must be http:// or https:// with a valid host (IP or DNS), e.g. http://192.168.1.10:8080/v1/embeddings", statusCode: 400);
        if (body.Embeddings?.Text?.Endpoint is not null && !IsValidEndpoint(body.Embeddings.Text.Endpoint))
            return Problem(title: $"Text embedding endpoint '{body.Embeddings.Text.Endpoint}' is not valid. Must be http:// or https:// with a valid host (IP or DNS)", statusCode: 400);
        // LLM endpoint: empty is allowed (= documentation generation disabled), non-empty must be a valid URL.
        if (!string.IsNullOrWhiteSpace(body.Llm?.Endpoint) && !IsValidEndpoint(body.Llm!.Endpoint!))
            return Problem(title: $"LLM endpoint '{body.Llm.Endpoint}' is not valid. Must be http:// or https:// with a valid host (IP or DNS), e.g. http://192.168.1.10:4000/v1", statusCode: 400);
        var flat = AppSettingsDto.Flatten(body);
        _logger.LogInformation("PUT /api/settings keys={Keys}", string.Join(",", flat.Keys));
        var updated = await _settings.UpsertAsync(flat, ct).ConfigureAwait(false);
        _configProvider.FireReload();
        return Ok(AppSettingsDto.FromConfiguration(_config, updated));
    }

    private static bool IsValidEndpoint(string endpoint)
    {
        var e = endpoint.Trim();
        if (string.IsNullOrWhiteSpace(e)) return false;
        if (!Uri.TryCreate(e, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (System.Net.IPAddress.TryParse(host, out _)) return true;
        // DNS hostname check (same as ProxyConfig)
        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (!System.Text.RegularExpressions.Regex.IsMatch(label, @"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")) return false;
        }
        return host.Length <= 253;
    }
}
