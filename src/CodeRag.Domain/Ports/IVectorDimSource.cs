namespace CodeRag.Domain.Ports;

/// <summary>
/// Источник размерности вектора для векторного индекса.
/// Позволяет SqliteVecVectorStore (Infrastructure) зависеть только от Domain,
/// не создавая циклическую зависимость с CodeRag.Embeddings.
/// </summary>
public interface IVectorDimSource
{
    /// <summary>Размерность вектора (auto-detected из API или из конфига).</summary>
    int Dimensions { get; }

    /// <summary>
    /// Длина контекста модели в токенах (auto-detected из /v1/models, n_ctx).
    /// null — если API не сообщает или недоступен.
    /// </summary>
    int? ContextLength { get; }
}
