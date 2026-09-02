using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeRag.Api.Models;

public sealed class RepositoriesSettingsDto
{
    public string? DefaultBranch { get; set; }
    public int? DefaultSyncIntervalSeconds { get; set; }
    public string? DefaultProxyId { get; set; }
    public bool? DefaultEnabled { get; set; }
}

public sealed class IndexingSettingsDto
{
    public int? SyncIntervalSeconds { get; set; }
    public int? MaxConcurrentIndexers { get; set; }
    public int? ParallelFileIndexers { get; set; }
    public bool? EnableFileWatcher { get; set; }
    public int? FileWatcherDebounceMs { get; set; }
    public int? MaxChunkChars { get; set; }
    public double? ChunkOverlapFraction { get; set; }
    public int? MinEmbeddingChars { get; set; }
    public string[]? Exclusions { get; set; }
    public string[]? LanguageExclusions { get; set; }
}

public sealed class EmbeddingProfileDto
{
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public int? Dimensions { get; set; }
    public int? BatchSize { get; set; }
    public int? TimeoutSeconds { get; set; }
}

public sealed class EmbeddingsSettingsDto
{
    public EmbeddingProfileDto? Code { get; set; }
    public EmbeddingProfileDto? Text { get; set; }
}

public sealed class SearchSettingsDto
{
    public double? LexicalWeight { get; set; }
    public double? SemanticWeight { get; set; }
    public double? StructuralWeight { get; set; }
    public int? DefaultResultCount { get; set; }
    public double? MinSemanticScore { get; set; }
}

public sealed class StorageSettingsDto
{
    public string? DataDir { get; set; }
    public string? VectorConfig { get; set; }
}

public sealed class SystemSettingsDto
{
    public string? LogLevel { get; set; }
}

/// <summary>Вложенная модель настроек — контракт Web UI (Settings.tsx).</summary>
public sealed class AppSettingsDto
{
    public RepositoriesSettingsDto? Repositories { get; set; }
    public IndexingSettingsDto? Indexing { get; set; }
    public EmbeddingsSettingsDto? Embeddings { get; set; }
    public SearchSettingsDto? Search { get; set; }
    public StorageSettingsDto? Storage { get; set; }
    public SystemSettingsDto? System { get; set; }

    /// <summary>Разворачивает DTO в плоские ключи "Group:Key" для таблицы settings.</summary>
    public static Dictionary<string, string> Flatten(AppSettingsDto dto)
    {
        var result = new Dictionary<string, string>();
        void Add(string key, string? value)
        {
            if (value is not null) result[key] = value;
        }

        if (dto.Repositories is { } r)
        {
            Add("Repositories:DefaultBranch", r.DefaultBranch);
            Add("Repositories:DefaultSyncIntervalSeconds", r.DefaultSyncIntervalSeconds?.ToString(CultureInfo.InvariantCulture));
            Add("Repositories:DefaultProxyId", r.DefaultProxyId);
            Add("Repositories:DefaultEnabled", r.DefaultEnabled?.ToString(CultureInfo.InvariantCulture));
        }

        if (dto.Indexing is { } i)
        {
            Add("Indexer:DefaultSyncIntervalSeconds", i.SyncIntervalSeconds?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:MaxConcurrentIndexers", i.MaxConcurrentIndexers?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:ParallelFileIndexers", i.ParallelFileIndexers?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:EnableFileWatcher", i.EnableFileWatcher?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:FileWatcherDebounceMs", i.FileWatcherDebounceMs?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:MaxChunkChars", i.MaxChunkChars?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:ChunkOverlapFraction", i.ChunkOverlapFraction?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:MinEmbeddingChars", i.MinEmbeddingChars?.ToString(CultureInfo.InvariantCulture));
            Add("Indexer:Exclusions", i.Exclusions is null ? null : string.Join(", ", i.Exclusions));
            Add("Indexer:LanguageExclusions", i.LanguageExclusions is null ? null : string.Join(", ", i.LanguageExclusions));
        }

        if (dto.Embeddings is { } e)
        {
            if (e.Code is { } c)
            {
                Add("Embeddings:Code:Endpoint", c.Endpoint);
                Add("Embeddings:Code:Model", c.Model);
                Add("Embeddings:Code:Dimensions", c.Dimensions?.ToString(CultureInfo.InvariantCulture));
                Add("Embeddings:Code:BatchSize", c.BatchSize?.ToString(CultureInfo.InvariantCulture));
                Add("Embeddings:Code:Timeout", c.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture));
            }
            if (e.Text is { } t)
            {
                Add("Embeddings:Text:Endpoint", t.Endpoint);
                Add("Embeddings:Text:Model", t.Model);
                Add("Embeddings:Text:Dimensions", t.Dimensions?.ToString(CultureInfo.InvariantCulture));
                Add("Embeddings:Text:BatchSize", t.BatchSize?.ToString(CultureInfo.InvariantCulture));
                Add("Embeddings:Text:Timeout", t.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (dto.Search is { } s)
        {
            Add("Search:LexicalWeight", s.LexicalWeight?.ToString(CultureInfo.InvariantCulture));
            Add("Search:SemanticWeight", s.SemanticWeight?.ToString(CultureInfo.InvariantCulture));
            Add("Search:StructuralWeight", s.StructuralWeight?.ToString(CultureInfo.InvariantCulture));
            Add("Search:DefaultResultCount", s.DefaultResultCount?.ToString(CultureInfo.InvariantCulture));
            Add("Search:MinSemanticScore", s.MinSemanticScore?.ToString(CultureInfo.InvariantCulture));
        }

        if (dto.Storage is { } st)
        {
            Add("Storage:VectorConfig", st.VectorConfig);
        }

        if (dto.System is { } sys)
        {
            Add("System:LogLevel", sys.LogLevel);
        }

        return result;
    }

    /// <summary>Собирает DTO из плоских значений (БД + merged IConfiguration с дефолтами).</summary>
    public static AppSettingsDto FromConfiguration(IConfiguration config, Dictionary<string, string> stored)
    {
        string? Get(string key) => stored.TryGetValue(key, out var v) ? v : null;

        int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        bool GetBool(string key, bool fallback) =>
            bool.TryParse(Get(key), out var v) ? v : fallback;

        string Cfg(string key, string fallback) =>
            !string.IsNullOrWhiteSpace(config[key]) ? config[key]! : fallback;

        int CfgInt(string key, int fallback) =>
            int.TryParse(config[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        double CfgDouble(string key, double fallback) =>
            double.TryParse(config[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        // Stored value wins over merged config; null if absent.
        string? Resolve(string key) => Get(key) ?? config[key];
        bool ResolveBool(string key, bool fallback) => bool.TryParse(Resolve(key), out var v) ? v : fallback;

        // Per-domain value, falling back to the legacy single-model key, then a default.
        string? DomStr(string domain, string key, string? legacyKey, string? fallback) =>
            Resolve($"Embeddings:{domain}:{key}") ?? (legacyKey is null ? null : Resolve(legacyKey)) ?? fallback;

        int DomInt(string domain, string key, string? legacyKey, int fallback) =>
            int.TryParse(DomStr(domain, key, legacyKey, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        return new AppSettingsDto
        {
            Repositories = new RepositoriesSettingsDto
            {
                DefaultBranch = Get("Repositories:DefaultBranch") ?? "main",
                DefaultSyncIntervalSeconds = GetInt("Repositories:DefaultSyncIntervalSeconds", 300),
                DefaultProxyId = Get("Repositories:DefaultProxyId"),
                DefaultEnabled = GetBool("Repositories:DefaultEnabled", true)
            },
            Indexing = new IndexingSettingsDto
            {
                SyncIntervalSeconds = CfgInt("Indexer:DefaultSyncIntervalSeconds", 300),
                MaxConcurrentIndexers = CfgInt("Indexer:MaxConcurrentIndexers", 2),
                ParallelFileIndexers = CfgInt("Indexer:ParallelFileIndexers", 8),
                EnableFileWatcher = ResolveBool("Indexer:EnableFileWatcher", false),
                FileWatcherDebounceMs = CfgInt("Indexer:FileWatcherDebounceMs", 1500),
                MaxChunkChars = CfgInt("Indexer:MaxChunkChars", 1500),
                ChunkOverlapFraction = CfgDouble("Indexer:ChunkOverlapFraction", 0.15),
                MinEmbeddingChars = CfgInt("Indexer:MinEmbeddingChars", 32),
                Exclusions = SplitList(Get("Indexer:Exclusions")),
                LanguageExclusions = SplitList(Get("Indexer:LanguageExclusions"))
            },
            Embeddings = new EmbeddingsSettingsDto
            {
                Code = new EmbeddingProfileDto
                {
                    Endpoint = DomStr("Code", "Endpoint", "Embeddings:Endpoint", CodeRag.Embeddings.EmbeddingOptions.DefaultCodeEndpoint),
                    Model = DomStr("Code", "Model", "Embeddings:Model", CodeRag.Embeddings.EmbeddingOptions.DefaultCodeModel),
                    Dimensions = DomInt("Code", "Dimensions", "Embeddings:Dimensions", CodeRag.Embeddings.EmbeddingOptions.DefaultCodeDimensions),
                    BatchSize = DomInt("Code", "BatchSize", "Embeddings:BatchSize", 32),
                    TimeoutSeconds = TimeoutSecondsFrom(DomStr("Code", "Timeout", "Embeddings:Timeout", null), 30)
                },
                Text = new EmbeddingProfileDto
                {
                    Endpoint = DomStr("Text", "Endpoint", "Embeddings:Endpoint", CodeRag.Embeddings.EmbeddingOptions.DefaultTextEndpoint),
                    Model = DomStr("Text", "Model", "Embeddings:Model", CodeRag.Embeddings.EmbeddingOptions.DefaultTextModel),
                    Dimensions = DomInt("Text", "Dimensions", "Embeddings:Dimensions", CodeRag.Embeddings.EmbeddingOptions.DefaultTextDimensions),
                    BatchSize = DomInt("Text", "BatchSize", "Embeddings:BatchSize", 32),
                    TimeoutSeconds = TimeoutSecondsFrom(DomStr("Text", "Timeout", "Embeddings:Timeout", null), 30)
                }
            },
            Search = new SearchSettingsDto
            {
                LexicalWeight = CfgDouble("Search:LexicalWeight", 0.4),
                SemanticWeight = CfgDouble("Search:SemanticWeight", 0.4),
                StructuralWeight = CfgDouble("Search:StructuralWeight", 0.2),
                DefaultResultCount = CfgInt("Search:DefaultResultCount", 20),
                MinSemanticScore = CfgDouble("Search:MinSemanticScore", 0.25)
            },
            Storage = new StorageSettingsDto
            {
                DataDir = Environment.GetEnvironmentVariable("CODERAG__DataDir")
                           ?? config["CODERAG:DataDir"]
                           ?? config["DataDir"]
                           ?? "/data",
                VectorConfig = Get("Storage:VectorConfig") ?? ""
            },
            System = new SystemSettingsDto
            {
                LogLevel = Get("System:LogLevel") ?? "Information"
            }
        };
    }

    private static string[]? SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int TimeoutSecondsFrom(string? raw, int fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        // Число трактуем как секунды; "00:00:30" — как TimeSpan
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) return (int)secs;
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts)) return (int)ts.TotalSeconds;
        return fallback;
    }
}
