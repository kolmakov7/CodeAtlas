using CodeRag.Domain.Enums;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Returns the vector store for a given embedding domain. Code and text use isolated vector
/// spaces (separate vec0 tables and dimensions), so their vectors are never mixed or compared.
/// </summary>
public interface IVectorStoreFactory
{
    /// <summary>Vector store for source-code embeddings.</summary>
    IVectorStore Code { get; }

    /// <summary>Vector store for text embeddings.</summary>
    IVectorStore Text { get; }

    /// <summary>Resolve the store for a domain. Unknown values resolve to <see cref="Code"/>.</summary>
    IVectorStore Get(EmbeddingDomain domain);
}

/// <summary>Default factory holding one store per domain.</summary>
public sealed class DefaultVectorStoreFactory : IVectorStoreFactory
{
    private readonly IVectorStore _code;
    private readonly IVectorStore _text;

    public DefaultVectorStoreFactory(IVectorStore code, IVectorStore text)
    {
        _code = code ?? throw new ArgumentNullException(nameof(code));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public IVectorStore Code => _code;
    public IVectorStore Text => _text;

    public IVectorStore Get(EmbeddingDomain domain) => domain == EmbeddingDomain.Text ? _text : _code;
}
