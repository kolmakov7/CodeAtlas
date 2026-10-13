using CodeRag.Documentation.Models;

namespace CodeRag.Documentation;

/// <summary>The outcome of a documentation generation for one target.</summary>
public sealed record DocumentationGenerationResult(
    string Content,
    string? Model,
    int ContextChars,
    int OutputChars,
    string PromptVersion)
{
    /// <summary>True when generation could not run (e.g. LLM not configured) and no content was produced.</summary>
    public bool IsFailure { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Turns a structured <see cref="DocumentationContext"/> into a Markdown document.
/// Implementations must never fabricate facts beyond the provided context.
/// </summary>
public interface IDocumentationGenerator
{
    /// <summary>True when generation can actually run (an LLM endpoint is configured).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Generate documentation for the given context. Throws <see cref="InvalidOperationException"/>
    /// when the LLM is unconfigured or the call ultimately fails; returns the normalized Markdown on success.
    /// </summary>
    Task<DocumentationGenerationResult> GenerateAsync(DocumentationContext context, CancellationToken ct = default);
}
