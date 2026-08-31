using System.Security.Cryptography;
using System.Text;
using CodeRag.Domain.Enums;

namespace CodeRag.Embeddings;

/// <summary>
/// Dual-embedding configuration container. Each domain (code / text) has its own independent
/// endpoint, model, and dimension profile so the two vector spaces can be served by different
/// OpenAI-compatible servers. The shared proxy settings apply to both.
///
/// Config layout (appsettings.json / env vars):
///   Embeddings:Code:Endpoint / Embeddings:Code:Model / Embeddings:Code:Dimensions ...
///   Embeddings:Text:Endpoint / Embeddings:Text:Model / Embeddings:Text:Dimensions ...
///   Embeddings:ProxyUrl, Embeddings:EmbeddingProxyId
///
/// A legacy single-model layout (top-level Embeddings:Endpoint etc.) is honoured as a fallback
/// when a domain has no endpoint of its own, and sensible per-domain defaults apply otherwise.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";
    public const string CodeSection = "Code";
    public const string TextSection = "Text";

    // Sensible defaults for each domain (overridable via config). Code = llama/bge-m3, Text = jina text.
    public const string DefaultCodeEndpoint = "http://code-embeddings:8080/v1/embeddings";
    public const string DefaultCodeModel = "bge-m3";
    public const int DefaultCodeDimensions = 1024;
    public const string DefaultTextEndpoint = "http://text-embeddings:8080/v1/embeddings";
    public const string DefaultTextModel = "jina-embeddings-v5-text-small";
    public const int DefaultTextDimensions = 1024;

    /// <summary>Code embedding profile (source code, comments stay code).</summary>
    public EmbeddingProfileOptions Code { get; set; } = new();

    /// <summary>Text embedding profile (Markdown, README, docs, prose).</summary>
    public EmbeddingProfileOptions Text { get; set; } = new();

    /// <summary>Optional per-repo embedding proxy identifier (informational / reserved).</summary>
    public string? EmbeddingProxyId { get; set; }

    /// <summary>Optional HTTP proxy URL used by the embedding HttpClient(s).</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>Returns the profile for a given embedding domain.</summary>
    public EmbeddingProfileOptions For(EmbeddingDomain domain) => domain == EmbeddingDomain.Text ? Text : Code;

    /// <summary>Convenience overload accepting the stored "code"/"text" token.</summary>
    public EmbeddingProfileOptions For(string? domain) => For(domain.ToDomain());

    /// <summary>
    /// Apply per-domain defaults for any domain that has no endpoint configured.
    /// Code → llama/bge-m3; Text → jina text. Idempotent.
    /// </summary>
    public void ApplyDomainDefaults()
    {
        if (string.IsNullOrWhiteSpace(Code.Endpoint))
        {
            Code.Endpoint ??= DefaultCodeEndpoint;
            Code.Model ??= DefaultCodeModel;
            if (Code.Dimensions <= 0) Code.Dimensions = DefaultCodeDimensions;
        }
        if (string.IsNullOrWhiteSpace(Text.Endpoint))
        {
            Text.Endpoint ??= DefaultTextEndpoint;
            Text.Model ??= DefaultTextModel;
            if (Text.Dimensions <= 0) Text.Dimensions = DefaultTextDimensions;
        }
    }
}

/// <summary>
/// A single embedding endpoint profile: where to call, which model, what dimension to expect,
/// and the retry/batching behaviour. Fully self-contained so code and text can differ.
/// </summary>
public sealed class EmbeddingProfileOptions
{
    /// <summary>Base endpoint for the OpenAI-compatible embeddings API. Null = not configured (defaults applied at registration).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model name to pass in the request body. Null = not configured.</summary>
    public string? Model { get; set; }

    /// <summary>Expected embedding dimension. 0 = unknown (auto-detect from first response).</summary>
    public int Dimensions { get; set; }

    /// <summary>Whether to L2-normalize the returned vectors. Default: true</summary>
    public bool Normalize { get; set; } = true;

    /// <summary>Optional prefix to prepend to each input text (e.g. "query: ").</summary>
    public string? Prefix { get; set; }

    /// <summary>Number of texts to send per HTTP request. Default: 32</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Per-request timeout (per batch, enforced via CancellationToken). Default: 30s</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max retry attempts on transient failure. Default: 3</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base backoff delay, grows exponentially per retry. Default: 200ms</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Optional API key sent in the Authorization header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Hash of the effective configuration. Used as the "model id" for vector invalidation:
    /// when this changes (endpoint / model / dimensions / batching), previously embedded
    /// vectors for this domain are considered stale and must be re-embedded.
    /// </summary>
    public string GetConfigHash()
    {
        var canonical = $"{Endpoint}|{Model}|{Dimensions}|{Normalize}|{Prefix}|{BatchSize}|{Timeout.TotalSeconds}|{MaxRetries}|{BaseDelay.TotalMilliseconds}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
