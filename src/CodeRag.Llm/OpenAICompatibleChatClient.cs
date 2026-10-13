using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Llm;

/// <summary>
/// Chat-completion client for any OpenAI-compatible endpoint (llama.cpp server, vLLM, Ollama
/// /v1, OpenAI, ...). Mirrors the retry/backoff behaviour of the embedding providers.
/// The model is NOT re-parsed from the URL; it is taken from options and sent in the body.
/// </summary>
public sealed class OpenAICompatibleChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmOptions _options;
    private readonly ILogger<OpenAICompatibleChatClient> _logger;

    public OpenAICompatibleChatClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<LlmOptions> options,
        ILogger<OpenAICompatibleChatClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.CurrentValue;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var opts = _options; // re-read per call so settings-table hot reload is honoured
        if (!opts.IsConfigured)
            throw new InvalidOperationException(
                "LLM endpoint is not configured. Set Llm:Endpoint (OpenAI-compatible base URL) to enable documentation generation.");
        if (string.IsNullOrWhiteSpace(request.UserPrompt) && string.IsNullOrWhiteSpace(request.SystemPrompt))
            throw new ArgumentException("Both system and user prompts are empty.", nameof(request));

        var url = opts.ResolveChatCompletionsUrl();
        var payload = BuildPayload(request, opts);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

        Exception? last = null;
        for (var attempt = 0; attempt <= Math.Max(0, opts.MaxRetries); attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromMilliseconds(opts.BaseDelayMs * Math.Pow(2, attempt - 1));
                _logger.LogInformation("LLM retry {Attempt} after {Delay}ms ({Last})", attempt, delay.TotalMilliseconds, last?.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.TimeoutSeconds)));

            try
            {
                var content = await SendAsync(json, attemptCts.Token).ConfigureAwait(false);
                return content;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"LLM request timed out after {opts.TimeoutSeconds}s");
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
            catch (JsonException ex)
            {
                // Non-retryable: the server answered but we could not parse it.
                throw new InvalidOperationException($"LLM returned an unparseable response: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException($"LLM request failed after retries: {last?.Message}", last);
    }

    private async Task<ChatCompletionResult> SendAsync(byte[] json, CancellationToken ct)
    {
        var opts = _options;
        using var client = _httpClientFactory.CreateClient(ServiceCollectionExtensions.ClientName);

        using var request = new HttpRequestMessage(HttpMethod.Post, opts.ResolveChatCompletionsUrl())
        {
            Content = new ByteArrayContent(json)
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opts.ApiKey}");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"LLM endpoint {response.StatusCode} ({(int)response.StatusCode}): {Truncate(body, 500)}");

        return ParseResponse(body);
    }

    private static object BuildPayload(ChatCompletionRequest request, LlmOptions opts)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        messages.Add(new { role = "user", content = request.UserPrompt });

        var payload = new Dictionary<string, object>
        {
            ["model"] = opts.Model ?? "local-model",
            ["messages"] = messages,
            ["temperature"] = request.Temperature ?? opts.Temperature,
            ["stream"] = false
        };
        var maxTokens = request.MaxTokens ?? opts.MaxTokens;
        if (maxTokens > 0) payload["max_tokens"] = maxTokens;
        return payload;
    }

    private static ChatCompletionResult ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? content = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                content = c.GetString();
            }
            else if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                content = text.GetString();
            }
        }

        int? promptTokens = root.TryGetProperty("usage", out var u) && u.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : null;
        int? completionTokens = root.TryGetProperty("usage", out var u2) && u2.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : null;
        string? model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        return new ChatCompletionResult(content ?? string.Empty, model, promptTokens, completionTokens);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
