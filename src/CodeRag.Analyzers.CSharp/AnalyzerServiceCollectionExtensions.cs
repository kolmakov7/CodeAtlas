using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.Abstractions.Chunking;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Analyzers.CSharp;

/// <summary>
/// DI регистрация анализаторов §13: IEnumerable&lt;ICodeAnalyzer&gt; выбор по CanAnalyze, порядок C# первым (дороже), затем остальные.
/// </summary>
public static class AnalyzerServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует все анализаторы Phase 3: CSharp (первым), TreeSitter (Python/JS/Go/C++), Sql, Vbs.
    /// </summary>
    public static IServiceCollection AddAnalyzers(this IServiceCollection services)
    {
        // Shared chunking (leaf, language-agnostic)
        services.AddSingleton<IChunkFactory, DefaultChunkFactory>();
        services.AddSingleton<ISourceSlicer>(sp => new DefaultSourceSlicer(sp.GetRequiredService<IChunkFactory>()));

        // C# первым — более дорогой, требует Roslyn/SemanticModel
        services.AddSingleton<ICodeAnalyzer, CSharpAnalyzer>();
        // Остальные — medium/syntax depth
        services.AddSingleton<ICodeAnalyzer, TreeSitter.TreeSitterAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, Sql.SqlAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, Vbs.VbsAnalyzer>();

        // Relation extractors (whole-repo, per language). C# uses Roslyn.
        services.AddSingleton<IRepositoryRelationExtractor, RoslynRelationExtractor>();
        services.AddSingleton<IRepositoryRelationExtractor, TreeSitter.Relations.PythonRelationExtractor>();
        services.AddSingleton<IRepositoryRelationExtractor, TreeSitter.Relations.JavaScriptRelationExtractor>();
        services.AddSingleton<IRepositoryRelationExtractor, TreeSitter.Relations.GoRelationExtractor>();
        services.AddSingleton<IRepositoryRelationExtractor, TreeSitter.Relations.CppRelationExtractor>();
        services.AddSingleton<IRepositoryRelationExtractor, Sql.SqlRelationExtractor>();
        return services;
    }

    /// <summary>Получить упорядоченный список анализаторов (C# первым).</summary>
    public static IEnumerable<ICodeAnalyzer> GetOrderedAnalyzers(IServiceProvider provider)
        => provider.GetServices<ICodeAnalyzer>().OrderBy(a => a.Language == SupportedLanguages.CSharp ? 0 : 1);
}
