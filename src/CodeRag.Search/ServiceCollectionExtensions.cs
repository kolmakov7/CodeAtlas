using CodeRag.Search.Services;
using CodeRag.Search.Strategies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeRag.Search;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSearch(this IServiceCollection services, Action<SearchOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        else services.Configure<SearchOptions>(_ => { });

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

    public static IServiceCollection AddSearch(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration, string sectionName = SearchOptions.SectionName)
    {
        services.Configure<SearchOptions>(configuration.GetSection(sectionName));
        return services.AddSearch();
    }
}
