using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;

namespace CodeRag.Embeddings;

/// <summary>
/// Default <see cref="IVectorDimSourceProvider"/> bridging the embedding provider factory to the
/// per-domain dim sources consumed by the vector-store factory.
/// </summary>
public sealed class DefaultVectorDimSourceProvider : IVectorDimSourceProvider
{
    private readonly IEmbeddingProviderFactory _factory;

    public DefaultVectorDimSourceProvider(IEmbeddingProviderFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IVectorDimSource? Get(EmbeddingDomain domain) => _factory.Get(domain);
}

/// <summary>
/// Default <see cref="IEmbeddingProviderFactory"/> holding one provider per domain.
/// </summary>
public sealed class DefaultEmbeddingProviderFactory : IEmbeddingProviderFactory
{
    private readonly IEmbeddingProvider _code;
    private readonly IEmbeddingProvider _text;

    public DefaultEmbeddingProviderFactory(IEmbeddingProvider code, IEmbeddingProvider text)
    {
        _code = code ?? throw new ArgumentNullException(nameof(code));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public IEmbeddingProvider Code => _code;
    public IEmbeddingProvider Text => _text;

    public IEmbeddingProvider Get(EmbeddingDomain domain) => domain == EmbeddingDomain.Text ? _text : _code;
}
