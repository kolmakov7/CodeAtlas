using CodeRag.Documentation.Models;
using CodeRag.Documentation.Prompts;
using CodeRag.Domain.Enums;
using CodeRag.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Documentation;

/// <summary>
/// Documentation generator backed by an OpenAI-compatible chat-completion endpoint.
/// The LLM is used purely as a prose renderer over the already-built context — it is not asked to
/// analyse code independently, which is what keeps the output grounded and low-hallucination.
/// </summary>
public sealed class LlmDocumentationGenerator : IDocumentationGenerator
{
    private readonly IChatCompletionClient _client;
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ILogger<LlmDocumentationGenerator> _logger;

    public LlmDocumentationGenerator(
        IChatCompletionClient client,
        IOptionsMonitor<LlmOptions> options,
        ILogger<LlmDocumentationGenerator> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable => _client.IsConfigured;

    public async Task<DocumentationGenerationResult> GenerateAsync(DocumentationContext context, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue; // hot reload via settings table
        if (!_client.IsConfigured)
            throw new InvalidOperationException(
                "LLM endpoint is not configured. Set Llm:Endpoint (OpenAI-compatible base URL) to enable documentation generation.");

        var contextText = context.ToPromptText();
        var truncated = false;
        if (opts.MaxContextChars > 0 && contextText.Length > opts.MaxContextChars)
        {
            contextText = contextText[..opts.MaxContextChars] + "\n\n[context truncated to fit model window]";
            truncated = true;
        }

        var systemPrompt = DocumentationPrompts.BuildSystemPrompt(context.Target);
        var userPrompt = DocumentationPrompts.BuildUserPrompt(context.Target, contextText);

        _logger.LogInformation(
            "Generating {Type} docs for {Name} (repo {Repo}) — context {Chars} chars{Trunc}",
            context.Target.Type.ToText(), context.Target.Name, context.RepositoryName,
            contextText.Length, truncated ? ", truncated" : string.Empty);

        var result = await _client.CompleteAsync(new ChatCompletionRequest(systemPrompt, userPrompt), ct).ConfigureAwait(false);

        var normalized = DocumentationValidator.Normalize(result.Content);
        if (!DocumentationValidator.LooksLikeMarkdown(normalized))
        {
            _logger.LogWarning("LLM output for {Name} did not look like Markdown; storing raw text.", context.Target.Name);
            // Keep whatever we got rather than failing the whole job; the entry will still be retrievable.
            normalized = string.IsNullOrWhiteSpace(normalized)
                ? (result.Content ?? string.Empty).Trim()
                : normalized;
        }

        return new DocumentationGenerationResult(
            Content: normalized,
            Model: result.Model ?? opts.Model,
            ContextChars: contextText.Length,
            OutputChars: normalized.Length,
            PromptVersion: DocumentationPrompts.CurrentVersion);
    }
}
