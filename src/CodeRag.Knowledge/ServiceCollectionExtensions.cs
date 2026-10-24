using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace CodeRag.Knowledge;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledge(
        this IServiceCollection services, IConfiguration configuration, bool enableScheduler = false)
    {
        services.Configure<KnowledgeOptions>(configuration.GetSection(KnowledgeOptions.SectionName));
        services.AddSingleton<Services.ProjectService>();
        services.AddSingleton<Services.TaskService>();
        services.AddSingleton<Services.KnowledgeEmbeddingService>();
        services.AddSingleton<Services.KnowledgeSearchService>();
        services.AddSingleton<Services.KnowledgeService>();
        services.AddSingleton<Services.RevisionService>();
        services.AddSingleton<Services.ConsolidationService>();
        if (enableScheduler)
            services.AddHostedService<Services.RevisionSchedulerService>();
        return services;
    }
}
