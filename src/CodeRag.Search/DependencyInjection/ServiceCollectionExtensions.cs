using CodeRag.Search.Services;
using CodeRag.Search.Strategies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeRag.Search.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSearch(this IServiceCollection services, IConfiguration? configuration = null, Action<SearchOptions>? configure = null)
    {
        if (configuration != null)
            services.Configure<SearchOptions>(configuration.GetSection(SearchOptions.SectionName));
        if (configure != null)
            services.Configure(configure);
        else if (configuration == null)
            services.Configure<SearchOptions>(_ => { });

        services.TryAddSingleton<ChunkLoader>();
        services.TryAddSingleton<SearchRanker>();
        services.TryAddSingleton<SymbolService>();
        services.TryAddSingleton<RelationService>();
        services.TryAddSingleton<FileContentService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISearchBranch, ExactSearchBranch>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISearchBranch, FtsSearchBranch>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISearchBranch, VectorSearchBranch>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISearchBranch, StructuralSearchBranch>());
        services.TryAddSingleton<ISearchService, SearchService>();

        return services;
    }

    public static IServiceCollection AddSearch(this IServiceCollection services, Action<SearchOptions> configure)
        => AddSearch(services, null, configure);
}
