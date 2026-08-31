namespace CodeRag.Embeddings;

/// <summary>
/// Абстракция провайдера эмбеддингов (indexing.md §5, ADR-005).
/// Совместим с OpenAI / llama.cpp / vLLM.
/// </summary>
public interface IEmbeddingProvider : CodeRag.Domain.Ports.IVectorDimSource
{
    /// <summary>Модель эмбеддингов (напр. bge-m3).</summary>
    string Model { get; }

    /// <summary>Хэш конфигурации (dimensions/normalize/prefix). Используется для инвалидации кэша.</summary>
    string ConfigHash { get; }

    /// <summary>
    /// Батчинг, retry, timeout, cancellation внутри.
    /// Порядок выходных векторов соответствует входным текстам.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
