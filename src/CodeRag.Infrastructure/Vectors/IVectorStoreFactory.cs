using CodeRag.Domain.Enums;

namespace CodeRag.Infrastructure.Vectors;

/// <summary>
/// Returns the vector store for a given embedding domain. Code and text use isolated vector
/// spaces (separate vec0 tables and dimensions), so their vectors are never mixed or compared.
/// Knowledge uses the text embedding provider but its own physical table.
/// </summary>
public interface IVectorStoreFactory
{
    /// <summary>Vector store for source-code embeddings.</summary>
    IVectorStore Code { get; }

    /// <summary>Vector store for text embeddings.</summary>
    IVectorStore Text { get; }

    /// <summary>Vector store for knowledge-entry embeddings (separate table, text provider).</summary>
    IVectorStore Knowledge { get; }

    /// <summary>Resolve the store for a domain. Unknown values resolve to <see cref="Code"/>.</summary>
    IVectorStore Get(EmbeddingDomain domain);
}

/// <summary>Default factory holding one store per domain.</summary>
public sealed class DefaultVectorStoreFactory : IVectorStoreFactory
{
    private readonly IVectorStore _code;
    private readonly IVectorStore _text;
    private readonly IVectorStore _knowledge;

    public DefaultVectorStoreFactory(IVectorStore code, IVectorStore text, IVectorStore? knowledge = null)
    {
        _code = code ?? throw new ArgumentNullException(nameof(code));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _knowledge = knowledge ?? text;
    }

    public IVectorStore Code => _code;
    public IVectorStore Text => _text;
    public IVectorStore Knowledge => _knowledge;

    public IVectorStore Get(EmbeddingDomain domain) => domain == EmbeddingDomain.Text ? _text : _code;
}
