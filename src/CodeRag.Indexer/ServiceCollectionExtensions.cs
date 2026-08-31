using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeRag.Indexer;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует Indexer: JobRunner (Channel+SemaphoreSlim+persistence), IndexingService, BackgroundService polling+FS watcher.
    /// Зависит от AddInfrastructure (stores, FTS, vector) и AddEmbeddings (IEmbeddingProvider).
    /// </summary>
    public static IServiceCollection AddIndexer(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
            services.Configure<IndexerOptions>(configuration.GetSection(IndexerOptions.SectionName));
        else
            services.Configure<IndexerOptions>(_ => { });

        // Persistence — register both concrete and port
        services.AddSingleton<IndexingJobStore>();
        services.AddSingleton<CodeRag.Domain.Ports.IIndexingJobStore>(sp => sp.GetRequiredService<IndexingJobStore>());

        // Git
        services.AddSingleton<IGitService, CliGitService>();

        // Core indexing
        services.AddSingleton<IndexingService>();

        // Job runner (Channel + SemaphoreSlim + persistence)
        services.AddSingleton<JobRunner>();
        services.AddSingleton<IJobRunner>(sp => sp.GetRequiredService<JobRunner>());

        // Background services: polling + FS watcher debounce
        services.AddHostedService<IndexingBackgroundService>();
        services.AddHostedService<FileWatcherService>();

        return services;
    }

    /// <summary>Перегрузка с Action для тестов без IConfiguration.</summary>
    public static IServiceCollection AddIndexer(this IServiceCollection services, Action<IndexerOptions> configure)
    {
        services.Configure(configure);
        return services.AddIndexer((IConfiguration?)null);
    }
}
