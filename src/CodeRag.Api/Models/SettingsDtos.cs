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
    public string[]? Exclusions { get; set; }
    public string[]? LanguageExclusions { get; set; }
}

public sealed class EmbeddingsSettingsDto
{
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public int? Dimensions { get; set; }
    public int? BatchSize { get; set; }
    public int? TimeoutSeconds { get; set; }
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
            Add("Indexer:Exclusions", i.Exclusions is null ? null : string.Join(", ", i.Exclusions));
            Add("Indexer:LanguageExclusions", i.LanguageExclusions is null ? null : string.Join(", ", i.LanguageExclusions));
        }

        if (dto.Embeddings is { } e)
        {
            Add("Embeddings:Endpoint", e.Endpoint);
            Add("Embeddings:Model", e.Model);
            Add("Embeddings:Dimensions", e.Dimensions?.ToString(CultureInfo.InvariantCulture));
            Add("Embeddings:BatchSize", e.BatchSize?.ToString(CultureInfo.InvariantCulture));
            Add("Embeddings:Timeout", e.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture));
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
                Exclusions = SplitList(Get("Indexer:Exclusions")),
                LanguageExclusions = SplitList(Get("Indexer:LanguageExclusions"))
            },
            Embeddings = new EmbeddingsSettingsDto
            {
                Endpoint = Cfg("Embeddings:Endpoint", "http://llama-embeddings:8080/v1/embeddings"),
                Model = Cfg("Embeddings:Model", "bge-m3"),
                Dimensions = CfgInt("Embeddings:Dimensions", 1024),
                BatchSize = CfgInt("Embeddings:BatchSize", 32),
                TimeoutSeconds = TimeoutSecondsFrom(config["Embeddings:Timeout"], 30)
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
