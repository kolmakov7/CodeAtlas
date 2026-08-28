using CodeRag.Analyzers.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Analyzers.Vbs;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVbsAnalyzer(this IServiceCollection services)
    {
        services.AddSingleton<ICodeAnalyzer, VbsAnalyzer>();
        return services;
    }
}
