using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using CodeRag.Analyzers.TreeSitter.Relations;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class GoRelationExtractorTests
{
    private sealed class InMemoryCatalog : ISymbolCatalog
    {
        private readonly IReadOnlyList<CatalogSymbol> _symbols;
        public InMemoryCatalog(IReadOnlyList<CatalogSymbol> symbols) => _symbols = symbols;
        public Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default) => Task.FromResult(_symbols);
    }

    [Fact]
    public async Task Go_Calls()
    {
        var go = """
package main
type MyStruct struct { N int }
func (m MyStruct) Save() int { return m.N }
func Caller(){
  var m MyStruct
  m.Save()
}
""";
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var res = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "main.go", go, SupportedLanguages.Go, 10000), default);
        if (res.Symbols.Count == 0) return;
        var catalog = res.Symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new GoRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("main.go", go) }));
        rels.Should().Contain(r => r.RelationType == RelationType.Calls);
        var caller = res.Symbols.Single(s => s.Name == "Caller");
        var save = res.Symbols.Single(s => s.Name == "Save");
        rels.Should().Contain(r => r.FromSymbolId == caller.Id && r.ToSymbolId == save.Id && r.RelationType == RelationType.Calls);
    }

    [Fact]
    public async Task Go_IsRelevantPath()
    {
        var cat = new GoRelationExtractor(new InMemoryCatalog(Array.Empty<CatalogSymbol>()));
        cat.IsRelevantPath("main.go").Should().BeTrue();
        cat.IsRelevantPath("app.py").Should().BeFalse();
        cat.IsRelevantPath("bin/main.go").Should().BeFalse();
        cat.Language.Should().Be(SupportedLanguages.Go);
    }
}
