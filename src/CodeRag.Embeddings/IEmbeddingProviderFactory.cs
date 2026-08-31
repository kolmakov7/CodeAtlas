using CodeRag.Domain.Enums;

namespace CodeRag.Embeddings;

/// <summary>
/// Returns the embedding provider for a given domain. Code and text are independent:
/// different endpoints, models, dimensions, caches, and retry behaviour.
/// </summary>
public interface IEmbeddingProviderFactory
{
    /// <summary>Provider for source-code chunks (comments stay code).</summary>
    IEmbeddingProvider Code { get; }

    /// <summary>Provider for text chunks (Markdown, README, docs, prose).</summary>
    IEmbeddingProvider Text { get; }

    /// <summary>Resolve the provider for a domain. Unknown values resolve to <see cref="IEmbeddingProvider.Code"/>.</summary>
    IEmbeddingProvider Get(EmbeddingDomain domain);
}
