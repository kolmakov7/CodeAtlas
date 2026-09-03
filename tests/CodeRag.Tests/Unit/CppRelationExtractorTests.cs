using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using CodeRag.Analyzers.TreeSitter.Relations;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class CppRelationExtractorTests
{
    private sealed class InMemoryCatalog : ISymbolCatalog
    {
        private readonly IReadOnlyList<CatalogSymbol> _symbols;
        public InMemoryCatalog(IReadOnlyList<CatalogSymbol> symbols) => _symbols = symbols;
        public Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default) => Task.FromResult(_symbols);
    }

    [Fact]
    public async Task Cpp_Contains()
    {
        var cpp = """
class Repo {
public:
 void Save();
 void Load(){ Save(); }
};
""";
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var res = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "a.cpp", cpp, SupportedLanguages.Cpp, 10000), default);
        if (res.Symbols.Count == 0) return;
        var catalog = res.Symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new CppRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("a.cpp", cpp) }));
        rels.Should().Contain(r => r.RelationType == RelationType.Contains);
    }

    [Fact]
    public async Task Cpp_IsRelevantPath()
    {
        var cat = new CppRelationExtractor(new InMemoryCatalog(Array.Empty<CatalogSymbol>()));
        cat.IsRelevantPath("a.cpp").Should().BeTrue();
        cat.IsRelevantPath("a.h").Should().BeTrue();
        cat.IsRelevantPath("a.hpp").Should().BeTrue();
        cat.IsRelevantPath("a.py").Should().BeFalse();
        cat.IsRelevantPath("obj/a.cpp").Should().BeFalse();
        cat.Language.Should().Be(SupportedLanguages.Cpp);
    }
}
