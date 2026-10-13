using CodeRag.Domain.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Documentation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the documentation-generation stack (context builder, LLM-backed generator, and the
    /// orchestration service). Requires the ports it consumes (ISymbolStore/IFileStore/IRelationStore/
    /// IRepositoryStore/ISearchService) and IChatCompletionClient (via AddLlm) to already be registered.
    /// </summary>
    public static IServiceCollection AddDocumentation(this IServiceCollection services)
    {
        // All members are stateless (only singleton port dependencies), so they are registered as
        // singletons — this lets the background DocumentationJobHandler (singleton) depend on the
        // orchestration service directly without a scope/captive-dependency problem.
        services.AddSingleton<IDocumentationContextBuilder, DocumentationContextBuilder>();
        services.AddSingleton<IDocumentationGenerator, LlmDocumentationGenerator>();
        services.AddSingleton<ITargetContentHasher, TargetContentHasher>();
        services.AddSingleton<DocumentationService>();
        return services;
    }
}
