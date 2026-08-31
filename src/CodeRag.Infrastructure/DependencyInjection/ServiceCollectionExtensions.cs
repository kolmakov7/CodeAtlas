using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Persistence.Repositories;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Stores;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует инфраструктуру хранения (SQLite + FTS5 + sqlite-vec) с dataDir = /data.
    /// Создает SqliteConnectionFactory, DbInitializer, Stores, FTS, VectorStore.
    /// </summary>
    /// <param name="services">DI контейнер</param>
    /// <param name="dataDir">директория для coderag.db (например, /data или ./data)</param>
    /// <param name="vecExtensionPath">опциональный путь к vec0.so; если null — авто-поиск</param>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string dataDir, string? vecExtensionPath = null)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir must not be empty.", nameof(dataDir));

        var fullDataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(fullDataDir);
        var dbPath = Path.Combine(fullDataDir, "coderag.db");

        services.AddSingleton<SqliteConnectionFactory>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqliteConnectionFactory>>();
            return new SqliteConnectionFactory(dbPath, logger, vecExtensionPath);
        });
        services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqliteConnectionFactory>());

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IDbConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<DbInitializer>>();
            // DbInitializer expects SqliteConnectionFactory concrete for legacy compat; pass singleton concrete if needed
            var concrete = sp.GetRequiredService<SqliteConnectionFactory>();
            return new DbInitializer(concrete, logger);
        });

        // Stores — register both Domain ports and legacy Infrastructure proxies
        services.AddSingleton<CodeRag.Domain.Ports.IRepositoryStore, RepositoryStore>();
        services.AddSingleton<CodeRag.Infrastructure.Stores.IRepositoryStore, RepositoryStore>(sp => (RepositoryStore)sp.GetRequiredService<CodeRag.Domain.Ports.IRepositoryStore>());
        services.AddSingleton<CodeRag.Domain.Ports.IFileStore, FileStore>();
        services.AddSingleton<CodeRag.Infrastructure.Stores.IFileStore, FileStore>(sp => (FileStore)sp.GetRequiredService<CodeRag.Domain.Ports.IFileStore>());
        services.AddSingleton<CodeRag.Domain.Ports.ISymbolStore, SymbolStore>();
        services.AddSingleton<CodeRag.Infrastructure.Stores.ISymbolStore, SymbolStore>(sp => (SymbolStore)sp.GetRequiredService<CodeRag.Domain.Ports.ISymbolStore>());
        services.AddSingleton<CodeRag.Domain.Ports.IChunkStore, ChunkStore>();
        services.AddSingleton<CodeRag.Infrastructure.Stores.IChunkStore, ChunkStore>(sp => (ChunkStore)sp.GetRequiredService<CodeRag.Domain.Ports.IChunkStore>());
        services.AddSingleton<CodeRag.Domain.Ports.IRelationStore, RelationStore>();
        services.AddSingleton<CodeRag.Analyzers.Abstractions.ISymbolCatalog, DapperSymbolCatalog>();
        services.AddSingleton<IIndexingErrorStore, IndexingErrorStore>();

        // Persistence adapters (Activity, Settings, Statistics, Health)
        services.AddSingleton<IActivityRepository, DapperActivityRepository>();
        services.AddSingleton<ISettingsRepository, DapperSettingsRepository>();
        services.AddSingleton<IStatisticsQueryService, DapperStatisticsQueryService>();
        services.AddSingleton<IDbHealthProbe, DbHealthProbe>();
        services.AddSingleton<IStorageProbe, StorageProbe>();

        // FTS
        services.AddSingleton<FtsHelper>();

        // Vector stores: one isolated store per domain (code / text) with InMemory fallback.
        // Per-domain dim sources are looked up by name (registered by AddEmbeddings); when absent
        // (embeddings not configured) a no-op source is used so the store defers table creation.
        services.AddSingleton<IVectorStoreFactory>(sp =>
        {
            var factory = sp.GetRequiredService<SqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<SqliteVecVectorStore>>();
            var fallbackLogger = sp.GetRequiredService<ILogger<InMemoryVectorStore>>();

            var dimProvider = sp.GetService<IVectorDimSourceProvider>();
            var codeDim = dimProvider?.Get(EmbeddingDomain.Code) ?? sp.GetService<IVectorDimSource>() ?? new CodeRag.Infrastructure.Vectors.NullDimSource();
            var textDim = dimProvider?.Get(EmbeddingDomain.Text) ?? new CodeRag.Infrastructure.Vectors.NullDimSource();

            var codeStore = new SqliteVecVectorStore(factory, codeDim, EmbeddingDomain.Code, logger, fallbackLogger);
            var textStore = new SqliteVecVectorStore(factory, textDim, EmbeddingDomain.Text, logger, fallbackLogger);
            return new DefaultVectorStoreFactory(codeStore, textStore);
        });

        // Back-compat: the single IVectorStore resolves to the Code store.
        services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<IVectorStoreFactory>().Code);

        // Also expose InMemory directly for diagnostics (optional)
        services.AddSingleton<InMemoryVectorStore>(sp =>
            new InMemoryVectorStore(sp.GetRequiredService<ILogger<InMemoryVectorStore>>()));

        return services;
    }

    /// <summary>
    /// Инициализирует БД (применяет миграции). Удобно вызывать на старте хоста.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider provider, CancellationToken ct = default)
    {
        var initializer = provider.GetRequiredService<DbInitializer>();
        await initializer.InitializeAsync(ct).ConfigureAwait(false);

        // Also ensure vec table if available (via vector store)
        var vec = provider.GetService<IVectorStore>() as SqliteVecVectorStore;
        if (vec is not null)
        {
            // Trigger lazy init with dummy empty call
            await vec.SearchAsync(new float[384], 1, null, ct).ConfigureAwait(false);
        }
    }
}
