namespace CodeRag.Llm;

/// <summary>
/// Configuration for the chat-completion (LLM) endpoint used by documentation generation.
/// Mirrors the OpenAI-compatible pattern already used by the embedding providers.
///
/// Config layout (appsettings.json / env / settings table):
///   Llm:Endpoint          base URL of an OpenAI-compatible server, e.g. http://llm:8080/v1
///   Llm:Model             model name sent in the request body
///   Llm:Temperature       sampling temperature (0.0 – 2.0)
///   Llm:MaxTokens         max completion tokens
///   Llm:TimeoutSeconds    per-request timeout
///   Llm:MaxRetries        retries on transient failure
///   Llm:ApiKey            optional bearer key
///   Llm:ProxyUrl          optional HTTP proxy
///   Llm:MaxContextChars   cap on the prompt context sent to the model (0 = unlimited)
///
/// When <see cref="Endpoint"/> is empty the LLM is considered unconfigured and generation
/// degrades gracefully (the feature reports "LLM not configured" instead of throwing).
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Default OpenAI-compatible endpoint (from the local opencode/gateway config).</summary>
    public const string DefaultEndpoint = "http://192.168.137.25:4000/v1";

    /// <summary>Default chat model used for documentation generation.</summary>
    public const string DefaultModel = "qwen3.8:27b";

    /// <summary>Base URL of the OpenAI-compatible server. Empty = unconfigured.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name to pass in the request body.</summary>
    public string? Model { get; set; } = DefaultModel;

    /// <summary>Sampling temperature.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>Maximum completion tokens. 0 = let the server decide.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Per-request timeout.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Max retry attempts on transient failure.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base backoff delay, grows exponentially per retry.</summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>Optional API key (Authorization: Bearer ...).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional HTTP proxy URL.</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>
    /// Hard cap on the total prompt context (system + user) in characters, used to trim
    /// the documentation context before sending it to the model. 0 = unlimited.
    /// </summary>
    public int MaxContextChars { get; set; } = 60000;

    /// <summary>True when an endpoint is configured (feature is enabled).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>
    /// Resolve the full chat-completions URL from the configured base endpoint.
    /// Accepts a bare host, a host with a /v1 suffix, or a full .../chat/completions URL.
    /// </summary>
    public string ResolveChatCompletionsUrl()
    {
        var ep = (Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (ep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return ep;
        if (ep.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return ep + "/chat/completions";
        return ep + "/v1/chat/completions";
    }
}
