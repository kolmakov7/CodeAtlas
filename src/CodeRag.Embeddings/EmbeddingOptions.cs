using System.Security.Cryptography;
using System.Text;

namespace CodeRag.Embeddings;

/// <summary>
/// Настройки эмбеддинг-провайдера (indexing.md §5, ADR-005, git-proxy-auth.md §2).
/// Endpoint по умолчанию: http://llama-embeddings:8080/v1/embeddings, модель bge-m3.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>OpenAI-compatible endpoint. Формат: http://host:port/v1/embeddings</summary>
    public string Endpoint { get; set; } = "http://llama-embeddings:8080/v1/embeddings";

    /// <summary>Имя модели (bge-m3 по умолчанию, см. indexing.md §5).</summary>
    public string Model { get; set; } = "bge-m3";

    /// <summary>Размерность (bge-m3 = 1024, но векторный индекс может использовать 384; хранится в ConfigHash).</summary>
    public int Dimensions { get; set; } = 1024;

    /// <summary>Нормализация векторов (влияет на ConfigHash).</summary>
    public bool Normalize { get; set; } = true;

    /// <summary>Опциональный префикс к тексту перед эмбеддингом (напр. "passage: ").</summary>
    public string? Prefix { get; set; }

    /// <summary>Размер батча для одного HTTP запроса.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Таймаут одного батча.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Максимум ретраев (exponential backoff).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Базовая задержка для backoff.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Per-repo прокси для эмбеддингов (settings embedding_proxy_id, git-proxy-auth.md §2).</summary>
    public string? EmbeddingProxyId { get; set; }

    /// <summary>Явный proxy URL для HttpClient (если не через proxy_id).</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>API key для OpenAI-совместимого сервера (опционально).</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Хэш конфигурации: sha256(dimensions|normalize|prefix) hex lowercase.
    /// Используется вместе с Model для инвалидации (chunks.embedding_config_hash).
    /// </summary>
    public string GetConfigHash()
    {
        var raw = $"{Dimensions}|{Normalize}|{Prefix ?? string.Empty}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
