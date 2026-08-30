using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Embeddings;

/// <summary>
/// OpenAI-compatible провайдер эмбеддингов с батчингом, таймаутом, retry (exponential backoff) и cancellation.
/// Endpoint: http://llama-embeddings:8080/v1/embeddings, модель bge-m3 (ADR-005, indexing.md §5).
/// Кэш по ContentHash+Model+ConfigHash реализован на уровне чанков (chunks.embedded_at) — провайдер также держит LRU
/// по хэшу текста+модель+конфиг для дедупликации батчей в рамках процесса.
/// </summary>
public sealed class OpenAICompatibleEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<OpenAICompatibleEmbeddingProvider> _logger;

    // Простой LRU in-memory кэш для дедупликации одинаковых текстов в рамках батча/процесса.
    // Ключ: sha256(content | model | configHash) -> vector
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, float[]> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private const int MaxCacheEntries = 2048;

    public string Model => _options.Model;
    public string ConfigHash => _options.GetConfigHash();
    public int Dimensions => _options.Dimensions;

    public OpenAICompatibleEmbeddingProvider(
        HttpClient httpClient,
        IOptions<EmbeddingOptions> options,
        ILogger<OpenAICompatibleEmbeddingProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new ArgumentException("Embedding endpoint must not be empty.", nameof(options));
        if (_options.BatchSize <= 0) _options.BatchSize = 32;
        if (_options.MaxRetries < 0) _options.MaxRetries = 0;
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));
        if (texts.Count == 0) return Array.Empty<float[]>();

        // Префикс применяется здесь, а не на вызывающей стороне
        var normalized = texts.Select(t => ApplyPrefix(t)).ToList();

        // Проверка LRU кэша: тексты с одинаковым ContentHash+Model+ConfigHash не требуют сети
        var result = new float[normalized.Count][];
        var toFetchIndices = new List<int>();
        var toFetchTexts = new List<string>();

        for (int i = 0; i < normalized.Count; i++)
        {
            var key = ComputeCacheKey(normalized[i]);
            float[]? cached = null;
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var vec))
                {
                    // Move to MRU
                    _lru.Remove(key);
                    _lru.AddLast(key);
                    cached = vec;
                }
            }
            if (cached is not null)
            {
                result[i] = cached;
            }
            else
            {
                toFetchIndices.Add(i);
                toFetchTexts.Add(normalized[i]);
            }
        }

        if (toFetchTexts.Count == 0)
        {
            _logger.LogDebug("Embedding cache hit for {Count} texts (model={Model}, config={Cfg})", texts.Count, Model, ConfigHash);
            return result;
        }

        // Батчинг
        var batches = Chunk(toFetchTexts, _options.BatchSize).ToList();
        var batchResults = new List<float[]>();
        int offset = 0;
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var vecs = await EmbedBatchWithRetryAsync(batch, ct).ConfigureAwait(false);
            batchResults.AddRange(vecs);
            offset += batch.Count;
        }

        // Заполнить result и закэшировать
        for (int i = 0; i < toFetchIndices.Count; i++)
        {
            var idx = toFetchIndices[i];
            var vec = batchResults[i];
            result[idx] = vec;

            var key = ComputeCacheKey(normalized[idx]);
            lock (_cacheLock)
            {
                if (!_cache.ContainsKey(key))
                {
                    _cache[key] = vec;
                    _lru.AddLast(key);
                    if (_cache.Count > MaxCacheEntries)
                    {
                        var oldest = _lru.First!.Value;
                        _lru.RemoveFirst();
                        _cache.Remove(oldest);
                    }
                }
            }
        }

        return result;
    }

    private string ApplyPrefix(string text)
    {
        if (string.IsNullOrEmpty(_options.Prefix)) return text ?? string.Empty;
        return _options.Prefix + (text ?? string.Empty);
    }

    private string ComputeCacheKey(string text)
    {
        var raw = $"{text}\0{Model}\0{ConfigHash}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchWithRetryAsync(IReadOnlyList<string> batch, CancellationToken ct)
    {
        var attempt = 0;
        var delay = _options.BaseDelay;

        while (true)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            try
            {
                return await DoEmbedBatchAsync(batch, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Внешняя отмена — не ретраим
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Таймаут батча — ретраим если есть попытки
                if (attempt >= _options.MaxRetries)
                {
                    _logger.LogWarning(ex, "Embedding batch timeout after {Attempt} attempts (size={Size}, model={Model})", attempt + 1, batch.Count, Model);
                    throw new TimeoutException($"Embedding timeout after {attempt + 1} attempts (model={Model}, size={batch.Count})", ex);
                }
                _logger.LogWarning(ex, "Embedding batch timeout, retry {Attempt}/{Max} after {Delay}ms", attempt + 1, _options.MaxRetries, delay.TotalMilliseconds);
            }
            catch (HttpRequestException ex) when (IsTransient(ex) && attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Transient embedding error, retry {Attempt}/{Max} after {Delay}ms (model={Model})", attempt + 1, _options.MaxRetries, delay.TotalMilliseconds, Model);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= _options.MaxRetries)
                {
                    _logger.LogError(ex, "Embedding request failed after {Attempt} attempts (model={Model}, endpoint={Endpoint})", attempt + 1, Model, _options.Endpoint);
                    throw;
                }
                _logger.LogWarning(ex, "Embedding request failed, retry {Attempt}/{Max}", attempt + 1, _options.MaxRetries);
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransientStatus(ex))
            {
                _logger.LogWarning(ex, "Transient embedding status, retry {Attempt}/{Max}", attempt + 1, _options.MaxRetries);
            }

            attempt++;
            if (attempt > _options.MaxRetries) throw new HttpRequestException($"Embedding failed after {attempt} attempts (model={Model})");

            // Exponential backoff с jitter 0.8..1.2
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            var wait = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitter);
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
        }
    }

    private static bool IsTransient(HttpRequestException ex)
    {
        // Сеть / 5xx / таймаут — считаем transient. 4xx кроме 429 — нет.
        if (ex.StatusCode is null) return true;
        var code = (int)ex.StatusCode;
        return code == 429 || (code >= 500 && code < 600);
    }

    private static bool IsTransientStatus(Exception ex)
    {
        if (ex is HttpRequestException hre && hre.StatusCode is not null)
        {
            var c = (int)hre.StatusCode;
            return c == 429 || (c >= 500 && c < 600);
        }
        return false;
    }

    private async Task<IReadOnlyList<float[]>> DoEmbedBatchAsync(IReadOnlyList<string> batch, CancellationToken ct)
    {
        var payload = new EmbeddingRequest
        {
            Model = _options.Model,
            Input = batch.ToList(),
            EncodingFormat = "float"
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        // Proxy per-repo уже применён на уровне HttpClient handler (если настроен ProxyUrl);
        // здесь лишь логируем endpoint без секретов.
        _logger.LogDebug("Embedding batch request model={Model} size={Size} endpoint={Endpoint}", Model, batch.Count, _options.Endpoint);

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var msg = $"Embedding API {resp.StatusCode} at {_options.Endpoint}: {Truncate(body, 500)}";
            _logger.LogWarning("Embedding API error {Status} model={Model} body={Body}", resp.StatusCode, Model, Truncate(body, 300));

            if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                throw new HttpRequestException(msg, null, resp.StatusCode);
            else
                throw new HttpRequestException(msg, null, resp.StatusCode);
        }

        var respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(respJson, JsonOptions);
        if (parsed?.Data is null || parsed.Data.Count == 0)
            throw new InvalidOperationException($"Empty embedding response from {_options.Endpoint}: {Truncate(respJson, 500)}");

        // Сортировка по index гарантирует порядок
        var ordered = parsed.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();

        if (ordered.Count != batch.Count)
            _logger.LogWarning("Embedding response count mismatch: requested {Req}, got {Got} (model={Model})", batch.Count, ordered.Count, Model);

        // Валидация размерности
        foreach (var vec in ordered)
        {
            if (vec.Length != _options.Dimensions)
                _logger.LogDebug("Embedding dim mismatch: expected {Exp}, got {Got} (model={Model}) — continuing", _options.Dimensions, vec.Length, Model);
        }

        return ordered;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static IReadOnlyList<IReadOnlyList<string>> Chunk(IReadOnlyList<string> src, int size)
    {
        var res = new List<IReadOnlyList<string>>();
        for (int i = 0; i < src.Count; i += size)
            res.Add(src.Skip(i).Take(size).ToList());
        return res;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private sealed class EmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";
        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = new();
        [JsonPropertyName("encoding_format")]
        public string? EncodingFormat { get; set; }
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }
        [JsonPropertyName("data")]
        public List<EmbeddingData> Data { get; set; } = new();
        [JsonPropertyName("model")]
        public string? Model { get; set; }
        [JsonPropertyName("usage")]
        public Usage? Usage { get; set; }
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }
        [JsonPropertyName("index")]
        public int Index { get; set; }
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
