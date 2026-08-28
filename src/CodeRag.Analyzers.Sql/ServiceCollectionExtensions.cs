using CodeRag.Analyzers.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Analyzers.Sql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlAnalyzer(this IServiceCollection services)
    {
        services.AddSingleton<ICodeAnalyzer, SqlAnalyzer>();
        return services;
    }
}
