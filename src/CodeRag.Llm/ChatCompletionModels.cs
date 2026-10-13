namespace CodeRag.Llm;

/// <summary>A single chat-completion request (system + user prompt).</summary>
public sealed record ChatCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    double? Temperature = null,
    int? MaxTokens = null);

/// <summary>The outcome of a chat completion.</summary>
public sealed record ChatCompletionResult(
    string Content,
    string? Model = null,
    int? PromptTokens = null,
    int? CompletionTokens = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Content);
}
