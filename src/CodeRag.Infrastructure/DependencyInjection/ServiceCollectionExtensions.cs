using CodeRag.Infrastructure.Persistence;
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

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqliteConnectionFactory>>();
            return new SqliteConnectionFactory(dbPath, logger, vecExtensionPath);
        });

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<SqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<DbInitializer>>();
            return new DbInitializer(factory, logger);
        });

        // Stores
        services.AddSingleton<IRepositoryStore, RepositoryStore>();
        services.AddSingleton<IFileStore, FileStore>();
        services.AddSingleton<ISymbolStore, SymbolStore>();
        services.AddSingleton<IChunkStore, ChunkStore>();

        // FTS
        services.AddSingleton<FtsHelper>();

        // Vector store: SqliteVec with InMemory fallback
        services.AddSingleton<IVectorStore>(sp =>
        {
            var factory = sp.GetRequiredService<SqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<SqliteVecVectorStore>>();
            var fallbackLogger = sp.GetRequiredService<ILogger<InMemoryVectorStore>>();
            return new SqliteVecVectorStore(factory, logger, fallbackLogger);
        });

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
