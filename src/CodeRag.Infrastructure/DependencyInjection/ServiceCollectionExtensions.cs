using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Persistence.Repositories;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Stores;
using CodeRag.Infrastructure.Vectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IDbConnectionFactory = CodeRag.Domain.Ports.IDbConnectionFactory;

namespace CodeRag.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует инфраструктуру хранения. Provider выбирается статически из конфигурации:
    ///   Storage:Provider = "sqlite" (default) | "postgresql".
    /// SQLite: main = &lt;dataDir&gt;/coderag.db, chunks = coderag-chunks.db, vec = coderag-vec.db.
    /// PostgreSQL: main/chunks/vec = 3 базы (Storage:Postgres:ConnectionString обязателен).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, string dataDir, string? vecExtensionPath = null)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("dataDir must not be empty.", nameof(dataDir));

        var provider = (configuration["Storage:Provider"] ?? "sqlite").Trim().ToLowerInvariant();
        if (provider is "postgres" or "postgresql")
        {
            var connectionString = configuration["Storage:Postgres:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "Storage:Provider=postgresql requires Storage:Postgres:ConnectionString (e.g. 'Host=localhost;Port=5432;Database=coderag;Username=coderag;Password=coderag').");

            services.AddSingleton<PostgresConnectionFactory>(sp =>
                new PostgresConnectionFactory(connectionString, sp.GetRequiredService<ILogger<PostgresConnectionFactory>>()));
            services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<PostgresConnectionFactory>());
        }
        else if (provider != "sqlite")
        {
            throw new InvalidOperationException($"Unknown Storage:Provider '{provider}' (expected 'sqlite' or 'postgresql').");
        }
        else
        {
            var fullDataDir = Path.GetFullPath(dataDir);
            Directory.CreateDirectory(fullDataDir);
            var dbPath = Path.Combine(fullDataDir, "coderag.db");

            services.AddSingleton<SqliteConnectionFactory>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<SqliteConnectionFactory>>();
                return new SqliteConnectionFactory(dbPath, logger, vecExtensionPath);
            });
            services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqliteConnectionFactory>());
        }

        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IDbConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<DbInitializer>>();
            return new DbInitializer(factory, logger);
        });

        services.AddSingleton<IRepositoryStore, RepositoryStore>();
        services.AddSingleton<IFileStore, FileStore>();
        services.AddSingleton<ISymbolStore, SymbolStore>();
        services.AddSingleton<IChunkStore, ChunkStore>();
        services.AddSingleton<CodeRag.Domain.Ports.IRelationStore, RelationStore>();
        services.AddSingleton<CodeRag.Domain.Ports.IRepositoryGroupStore, RepositoryGroupStore>();
        services.AddSingleton<CodeRag.Analyzers.Abstractions.ISymbolCatalog, DapperSymbolCatalog>();
        services.AddSingleton<IIndexingErrorStore, IndexingErrorStore>();

        // Persistence adapters (Activity, Settings, Statistics, Health)
        services.AddSingleton<IActivityRepository, DapperActivityRepository>();
        services.AddSingleton<ISettingsRepository, DapperSettingsRepository>();
        services.AddSingleton<IStatisticsQueryService, DapperStatisticsQueryService>();
        services.AddSingleton<IStatisticsRefresher, DapperStatisticsRefresher>();
        services.AddSingleton<IMcpUsageRecorder, DapperMcpUsageRecorder>();
        services.AddSingleton<IMcpUsageQueryService, DapperMcpUsageQueryService>();
        services.AddSingleton<IDbHealthProbe, DbHealthProbe>();
        services.AddSingleton<IStorageProbe, StorageProbe>();

        // FTS
        services.AddSingleton<FtsHelper>();

        // Vector stores: one isolated store per domain (code / text) with InMemory fallback.
        // Per-domain dim sources are looked up by name (registered by AddEmbeddings); when absent
        // (embeddings not configured) a no-op source is used so the store defers table creation.
        services.AddSingleton<IVectorStoreFactory>(sp =>
        {
            var factory = sp.GetRequiredService<IDbConnectionFactory>();
            var fallbackLogger = sp.GetRequiredService<ILogger<InMemoryVectorStore>>();

            var dimProvider = sp.GetService<IVectorDimSourceProvider>();
            var codeDim = dimProvider?.Get(EmbeddingDomain.Code) ?? sp.GetService<IVectorDimSource>() ?? new NullDimSource();
            var textDim = dimProvider?.Get(EmbeddingDomain.Text) ?? new NullDimSource();

            if (SqlDialect.IsPostgres(factory))
            {
                var logger = sp.GetRequiredService<ILogger<PgVectorVectorStore>>();
                return new DefaultVectorStoreFactory(
                    new PgVectorVectorStore(factory, codeDim, EmbeddingDomain.Code, logger, fallbackLogger),
                    new PgVectorVectorStore(factory, textDim, EmbeddingDomain.Text, logger, fallbackLogger));
            }

            var sqliteLogger = sp.GetRequiredService<ILogger<SqliteVecVectorStore>>();
            return new DefaultVectorStoreFactory(
                new SqliteVecVectorStore(factory, codeDim, EmbeddingDomain.Code, sqliteLogger, fallbackLogger),
                new SqliteVecVectorStore(factory, textDim, EmbeddingDomain.Text, sqliteLogger, fallbackLogger));
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

        // Load saved settings into IConfiguration NOW that the tables exist, so the code/text embedding
        // dimensions come from the settings table (not the built-in defaults). This MUST run before the
        // vector-store init below, otherwise the vec table is created with the default dim and marked STALE.
        provider.GetService<SettingsTableConfigurationProvider>()?.FireReload();

        // Also ensure vec table if available (via vector store) — now with the correct, settings-sourced dim.
        var vec = provider.GetService<IVectorStore>();
        if (vec is SqliteVecVectorStore or PgVectorVectorStore)
        {
            // Trigger lazy table creation. Table creation happens inside EnsureInitializedAsync before the
            // query, so the dummy vector dim may not match and a dim-mismatch here is non-fatal.
            try
            {
                await vec.SearchAsync(new float[384], 1, null, ct).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal: startup table creation is best-effort.
            }
        }

        // Ensure statistics snapshots exist (first start after migration / legacy DB). Non-fatal.
        try
        {
            await provider.GetRequiredService<IStatisticsRefresher>().EnsureAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("StatisticsRefresher");
            logger.LogWarning(ex, "Startup statistics refresh failed — statistics will fall back to live aggregation until the next refresh");
        }
    }
}
