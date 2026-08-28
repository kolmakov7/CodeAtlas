using CodeRag.Analyzers.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Analyzers.TreeSitter;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTreeSitterAnalyzers(this IServiceCollection services)
    {
        services.AddSingleton<ICodeAnalyzer, TreeSitterAnalyzer>();
        return services;
    }
}
