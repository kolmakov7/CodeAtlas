namespace CodeRag.Llm;

/// <summary>
/// Abstraction over an OpenAI-compatible chat-completion endpoint.
/// Implementations must be tolerant of an unconfigured endpoint by reporting
/// <see cref="IsConfigured"/> = false rather than throwing at call time.
/// </summary>
public interface IChatCompletionClient
{
    /// <summary>True when an endpoint is configured and generation is possible.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Run a chat completion. Throws on unrecoverable failure (bad request, exhausted retries,
    /// or unconfigured endpoint) so callers can map it to a job/entry error state.
    /// </summary>
    Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default);
}
