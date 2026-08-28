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

        services.TryAddSingleton<ISearchService, SearchService>();

        return services;
    }

    public static IServiceCollection AddSearch(this IServiceCollection services, Action<SearchOptions> configure)
        => AddSearch(services, null, configure);
}
