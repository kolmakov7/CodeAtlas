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
    private readonly IOptionsMonitor<EmbeddingOptions> _optionsMonitor;
    private readonly ILogger<OpenAICompatibleEmbeddingProvider> _logger;

    // Читаем опции при каждом обращении — hot reload из таблицы settings (FireReload → OptionsMonitor)
    private EmbeddingOptions Opts => _optionsMonitor.CurrentValue;

    // Простой LRU in-memory кэш для дедупликации одинаковых текстов в рамках батча/процесса.
    // Ключ: sha256(content | model | configHash) -> vector
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, float[]> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private const int MaxCacheEntries = 2048;

    public string Model => Opts.Model;
    public string ConfigHash => Opts.GetConfigHash();

    // Размерность может быть auto-detected из ответа API.
    // Возвращаем 0 если ещё не известна (до загрузки settings и до первого API вызова),
    // чтобы SqliteVecVectorStore отложил создание таблицы до получения реального dim.
    private int? _detectedDim;
    public int Dimensions => _detectedDim ?? Opts.Dimensions;

    // Длина контекста модели (n_ctx) — детектится ОДИН раз на комбинацию Endpoint|Model
    // из GET {base}/v1/models. При смене модели или ссылки на API — детект заново.
    private readonly object _modelInfoLock = new();
    private string? _modelInfoIdentity;
    private int? _detectedContextLength;
    public int? ContextLength => _detectedContextLength;

    public OpenAICompatibleEmbeddingProvider(
        HttpClient httpClient,
        IOptionsMonitor<EmbeddingOptions> options,
        ILogger<OpenAICompatibleEmbeddingProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _optionsMonitor = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(Opts.Endpoint))
            throw new ArgumentException("Embedding endpoint must not be empty.", nameof(options));
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));
        if (texts.Count == 0) return Array.Empty<float[]>();

        MaybeDetectModelInfo();

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
        var batches = Chunk(toFetchTexts, Opts.BatchSize).ToList();
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

            // Auto-detect dim from first non-empty response
            EnsureDetectedDim(vec.Length);

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

    private void EnsureDetectedDim(int observed)
    {
        if (observed <= 0) return;
        if (_detectedDim.HasValue)
        {
            if (_detectedDim.Value != observed)
                _logger.LogWarning("Embedding dim changed mid-run: {Old} -> {New} (model={Model})", _detectedDim.Value, observed, Model);
            return;
        }
        _detectedDim = observed;
        if (Opts.Dimensions > 0 && Opts.Dimensions != observed)
        {
            _logger.LogWarning("Configured embedding dim={Cfg} but API returns dim={Observed} (model={Model}) — using observed", Opts.Dimensions, observed, Model);
        }
        else
        {
            _logger.LogInformation("Auto-detected embedding dim={Dim} (model={Model})", observed, Model);
        }
    }

    /// <summary>
    /// Lazily detect the model context length (n_ctx) from GET {base}/v1/models once per Endpoint|Model identity.
    /// Runs out-of-band (fire-and-forget) so embedding latency is not affected; result cached until identity changes.
    /// </summary>
    private void MaybeDetectModelInfo()
    {
        var identity = $"{Opts.Endpoint}|{Opts.Model}";
        lock (_modelInfoLock)
        {
            if (string.Equals(_modelInfoIdentity, identity, StringComparison.Ordinal))
                return;
            _modelInfoIdentity = identity;
            _detectedContextLength = null;
            _ = DetectModelInfoAsync(identity, CancellationToken.None);
        }
    }

    /// <summary>Force re-detect and await model info (used by diagnostics/tests). Returns detected n_ctx or null.</summary>
    public async Task<int?> RefreshModelInfoAsync(CancellationToken ct = default)
    {
        var identity = $"{Opts.Endpoint}|{Opts.Model}";
        lock (_modelInfoLock)
        {
            _modelInfoIdentity = identity;
            _detectedContextLength = null;
        }
        await DetectModelInfoAsync(identity, ct).ConfigureAwait(false);
        return _detectedContextLength;
    }

    private async Task DetectModelInfoAsync(string identity, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var url = DeriveModelsUrl(Opts.Endpoint);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(Opts.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Opts.ApiKey);

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Model info endpoint {Url} returned {Status} (model={Model}) — context length not detected", url, (int)resp.StatusCode, Opts.Model);
                return;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var nCtx = ParseContextLength(json, Opts.Model);
            if (nCtx is > 0)
            {
                _detectedContextLength = nCtx;
                _logger.LogInformation("Detected model context length n_ctx={NCtx} (model={Model})", nCtx, Opts.Model);
            }
            else
            {
                _logger.LogWarning("Model info for {Model} did not expose context length (n_ctx) — chunking will use defaults", Opts.Model);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Model info detection timed out (model={Model})", Opts.Model);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Model info detection failed (model={Model}) — chunking will use defaults", Opts.Model);
        }
    }

    private static string DeriveModelsUrl(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/embeddings".Length] + "/models";
        return trimmed + "/models";
    }

    private static int? ParseContextLength(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement? modelEl = null;
        foreach (var key in new[] { "data", "models" })
        {
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (e.TryGetProperty("id", out var id) &&
                        string.Equals(id.GetString(), model, StringComparison.OrdinalIgnoreCase))
                    {
                        modelEl = e;
                        break;
                    }
                }
                // fallback: first element if no exact id match
                modelEl ??= arr.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                if (modelEl is not null) break;
            }
        }

        if (modelEl is null) return null;
        var el = modelEl.Value;

        // llama.cpp: meta.n_ctx
        if (el.TryGetProperty("meta", out var meta) && meta.TryGetProperty("n_ctx", out var nctx) && nctx.TryGetInt32(out var n))
            return n;
        // newer llama.cpp / some servers
        if (el.TryGetProperty("context_length", out var cl) && cl.TryGetInt32(out var c))
            return c;
        if (el.TryGetProperty("max_context_length", out var mcl) && mcl.TryGetInt32(out var mc))
            return mc;
        return null;
    }

    private string ApplyPrefix(string text)
    {
        if (string.IsNullOrEmpty(Opts.Prefix)) return text ?? string.Empty;
        return Opts.Prefix + (text ?? string.Empty);
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
        var delay = Opts.BaseDelay;

        while (true)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Opts.Timeout);

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
                if (attempt >= Opts.MaxRetries)
                {
                    _logger.LogWarning(ex, "Embedding batch timeout after {Attempt} attempts (size={Size}, model={Model})", attempt + 1, batch.Count, Model);
                    throw new TimeoutException($"Embedding timeout after {attempt + 1} attempts (model={Model}, size={batch.Count})", ex);
                }
                _logger.LogWarning(ex, "Embedding batch timeout, retry {Attempt}/{Max} after {Delay}ms", attempt + 1, Opts.MaxRetries, delay.TotalMilliseconds);
            }
            catch (HttpRequestException ex) when (IsTransient(ex) && attempt < Opts.MaxRetries)
            {
                _logger.LogWarning(ex, "Transient embedding error, retry {Attempt}/{Max} after {Delay}ms (model={Model})", attempt + 1, Opts.MaxRetries, delay.TotalMilliseconds, Model);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= Opts.MaxRetries)
                {
                    _logger.LogError(ex, "Embedding request failed after {Attempt} attempts (model={Model}, endpoint={Endpoint})", attempt + 1, Model, Opts.Endpoint);
                    throw;
                }
                _logger.LogWarning(ex, "Embedding request failed, retry {Attempt}/{Max}", attempt + 1, Opts.MaxRetries);
            }
            catch (Exception ex) when (attempt < Opts.MaxRetries && IsTransientStatus(ex))
            {
                _logger.LogWarning(ex, "Transient embedding status, retry {Attempt}/{Max}", attempt + 1, Opts.MaxRetries);
            }

            attempt++;
            if (attempt > Opts.MaxRetries) throw new HttpRequestException($"Embedding failed after {attempt} attempts (model={Model})");

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
            Model = Opts.Model,
            Input = batch.ToList(),
            EncodingFormat = "float"
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, Opts.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(Opts.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Opts.ApiKey);

        // Proxy per-repo уже применён на уровне HttpClient handler (если настроен ProxyUrl);
        // здесь лишь логируем endpoint без секретов.
        _logger.LogDebug("Embedding batch request model={Model} size={Size} endpoint={Endpoint}", Model, batch.Count, Opts.Endpoint);

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var msg = $"Embedding API {resp.StatusCode} at {Opts.Endpoint}: {Truncate(body, 500)}";
            _logger.LogWarning("Embedding API error {Status} model={Model} body={Body}", resp.StatusCode, Model, Truncate(body, 300));

            if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
                throw new HttpRequestException(msg, null, resp.StatusCode);
            else
                throw new HttpRequestException(msg, null, resp.StatusCode);
        }

        var respJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<EmbeddingResponse>(respJson, JsonOptions);
        if (parsed?.Data is null || parsed.Data.Count == 0)
            throw new InvalidOperationException($"Empty embedding response from {Opts.Endpoint}: {Truncate(respJson, 500)}");

        // Сортировка по index гарантирует порядок
        var ordered = parsed.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();

        if (ordered.Count != batch.Count)
            _logger.LogWarning("Embedding response count mismatch: requested {Req}, got {Got} (model={Model})", batch.Count, ordered.Count, Model);

        // Валидация размерности
        foreach (var vec in ordered)
        {
            if (vec.Length != Opts.Dimensions)
                _logger.LogDebug("Embedding dim mismatch: expected {Exp}, got {Got} (model={Model}) — continuing", Opts.Dimensions, vec.Length, Model);
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
