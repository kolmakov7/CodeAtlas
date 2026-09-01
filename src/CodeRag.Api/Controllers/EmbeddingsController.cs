using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace CodeRag.Api.Controllers;

[ApiController]
[Route("api/embeddings")]
public sealed class EmbeddingsController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EmbeddingsController> _logger;
    public EmbeddingsController(IHttpClientFactory httpFactory, ILogger<EmbeddingsController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public sealed record DetectRequest(string Endpoint, string? Model);
    public sealed record DetectResponse(string Endpoint, string? DetectedModel, int? DetectedDimensions, int? DetectedContextLength, bool Success, string? Error, string? RawModelsJson, IReadOnlyList<string>? AvailableModels);

    [HttpPost("detect")]
    public async Task<IActionResult> Detect([FromBody] DetectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint))
            return BadRequest("Endpoint required");
        if (!IsValidEndpoint(req.Endpoint))
            return BadRequest("Endpoint must be http:// or https:// with valid host");

        var endpoint = req.Endpoint.Trim();
        var modelHint = req.Model?.Trim();

        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);

            // Try to get model list first
            var modelsUrl = DeriveModelsUrl(endpoint);
            string? detectedModel = modelHint;
            int? ctxLen = null;
            string? rawJson = null;
            List<string> availableModels = new();
            if (modelsUrl is not null)
            {
                try
                {
                    using var getReq = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    using var resp = await client.SendAsync(getReq, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        rawJson = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        // Try to parse
                        try
                        {
                            using var doc = JsonDocument.Parse(rawJson);
                            var root = doc.RootElement;
                            JsonElement? first = null;
                            foreach (var key in new[] { "data", "models" })
                            {
                                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                                {
                                    // Collect all available model ids for this endpoint
                                    foreach (var e in arr.EnumerateArray())
                                    {
                                        if (e.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                                        {
                                            var mid = id.GetString()!;
                                            if (!availableModels.Contains(mid)) availableModels.Add(mid);
                                            if (!string.IsNullOrWhiteSpace(modelHint) && string.Equals(mid, modelHint, StringComparison.OrdinalIgnoreCase))
                                            {
                                                first = e;
                                            }
                                        }
                                    }
                                    first ??= arr.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                                    if (first is not null) break;
                                }
                            }
                            if (first is not null)
                            {
                                var el = first.Value;
                                if (string.IsNullOrWhiteSpace(detectedModel) && el.TryGetProperty("id", out var idEl))
                                    detectedModel = idEl.GetString();
                                if (el.TryGetProperty("meta", out var meta) && meta.TryGetProperty("n_ctx", out var nctx) && nctx.TryGetInt32(out var n))
                                    ctxLen = n;
                                else if (el.TryGetProperty("context_length", out var cl) && cl.TryGetInt32(out var c))
                                    ctxLen = c;
                            }
                        }
                        catch { /* ignore parse */ }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Models probe failed for {Endpoint}", endpoint); }
            }

            // Try to detect dimensions via a test embedding
            int? dims = null;
            var testModel = detectedModel ?? modelHint ?? "test";
            try
            {
                var payload = new { model = testModel, input = new[] { "hello" }, encoding_format = "float" };
                var json = JsonSerializer.Serialize(payload);
                using var postReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(TimeSpan.FromSeconds(6));
                using var resp2 = await client.SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, cts2.Token).ConfigureAwait(false);
                if (resp2.IsSuccessStatusCode)
                {
                    var body = await resp2.Content.ReadAsStringAsync(cts2.Token).ConfigureAwait(false);
                    try
                    {
                        using var doc2 = JsonDocument.Parse(body);
                        var root2 = doc2.RootElement;
                        if (root2.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        {
                            var first = data.EnumerateArray().FirstOrDefault();
                            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                            {
                                dims = emb.GetArrayLength();
                                // Also try to get model from response
                                if (root2.TryGetProperty("model", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                                    detectedModel ??= m.GetString();
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    var body = await resp2.Content.ReadAsStringAsync(cts2.Token).ConfigureAwait(false);
                    _logger.LogDebug("Embedding probe {Endpoint} returned {Status}: {Body}", endpoint, (int)resp2.StatusCode, body.Substring(0, Math.Min(300, body.Length)));
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Embedding probe failed for {Endpoint}", endpoint); }

            var success = dims is not null || !string.IsNullOrWhiteSpace(detectedModel) || availableModels.Count > 0;
            // Fallback: if we got a list but no single detectedModel, pick first available
            if (string.IsNullOrWhiteSpace(detectedModel) && availableModels.Count > 0) detectedModel = availableModels[0];
            return Ok(new DetectResponse(endpoint, detectedModel, dims, ctxLen, success, success ? null : "Could not auto-detect model/dimensions. Check endpoint is reachable and OpenAI-compatible.", rawJson, availableModels));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Detect failed for {Endpoint}", endpoint);
            return Ok(new DetectResponse(endpoint, null, null, null, false, ex.Message, null, null));
        }
    }

    private static bool IsValidEndpoint(string endpoint)
    {
        var e = endpoint.Trim();
        if (!Uri.TryCreate(e, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (System.Net.IPAddress.TryParse(host, out _)) return true;
        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (!System.Text.RegularExpressions.Regex.IsMatch(label, @"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$")) return false;
        }
        return host.Length <= 253;
    }

    private static string? DeriveModelsUrl(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/embeddings".Length] + "/models";
        return trimmed + "/models";
    }
}
