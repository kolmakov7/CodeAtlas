using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Search;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSearch(this IServiceCollection services, Action<SearchOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        else services.Configure<SearchOptions>(_ => { });

        services.AddSingleton<ISearchService, SearchService>();
        return services;
    }

    public static IServiceCollection AddSearch(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration, string sectionName = SearchOptions.SectionName)
    {
        services.Configure<SearchOptions>(configuration.GetSection(sectionName));
        return services.AddSearch();
    }
}
