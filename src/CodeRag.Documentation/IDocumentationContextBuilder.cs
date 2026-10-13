using CodeRag.Documentation.Models;

namespace CodeRag.Documentation;

/// <summary>
/// Assembles a <see cref="DocumentationContext"/> for a target strictly from the existing index.
/// Extracted so <see cref="DocumentationService"/> can be unit-tested against a fake builder.
/// </summary>
public interface IDocumentationContextBuilder
{
    Task<DocumentationContext> BuildAsync(
        DocumentationTarget target,
        DocumentationBuildOptions? options = null,
        CancellationToken ct = default);
}
