using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using CodeRag.Analyzers.TreeSitter.Relations;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class PythonRelationExtractorTests
{
    private sealed class InMemoryCatalog : ISymbolCatalog
    {
        private readonly IReadOnlyList<CatalogSymbol> _symbols;
        public InMemoryCatalog(IReadOnlyList<CatalogSymbol> symbols) => _symbols = symbols;
        public Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default) => Task.FromResult(_symbols);
    }

    private static async Task<(IReadOnlyList<Symbol> Symbols, IReadOnlyList<CatalogSymbol> Catalog, TreeSitterAnalyzer Analyzer)> AnalyzeAsync(string code, string path = "app.py")
    {
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var ctx = new FileContext(repoId, null, path, code, SupportedLanguages.Python, 10000);
        var res = await analyzer.AnalyzeAsync(ctx, default);
        var symbols = res.Symbols;
        var catalog = symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        return (symbols, catalog, analyzer);
    }

    [Fact]
    public async Task Python_Contains_And_Calls_SingleFile()
    {
        var code = """
class OrderService:
    def save(self, item):
        print(item)
    def process(self, item):
        self.save(item)
        helper(item)

def helper(x):
    return x
""";
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var res = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "app.py", code, SupportedLanguages.Python, 10000), default);
        if (res.Symbols.Count == 0) return; // native unavailable -> fallback, skip
        var catalog = res.Symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new PythonRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("app.py", code) }));
        rels.Should().NotBeEmpty();
        var sym = res.Symbols.ToDictionary(s => s.Name, s => s.Id);
        rels.Should().Contain(r => r.RelationType == RelationType.Contains);
        rels.Should().Contain(r => r.RelationType == RelationType.Calls);
        // OrderService contains save/process
        var orderService = res.Symbols.Single(s => s.Name == "OrderService");
        var save = res.Symbols.Single(s => s.Name == "save");
        rels.Should().Contain(r => r.FromSymbolId == orderService.Id && r.ToSymbolId == save.Id && r.RelationType == RelationType.Contains);
    }

    [Fact]
    public async Task Python_Calls_CrossFile()
    {
        var py1 = "def caller():\n  helper(1)\n";
        var py2 = "def helper(x):\n  return x\n";
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var r1 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "a.py", py1, SupportedLanguages.Python, 10000), default);
        var r2 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "b.py", py2, SupportedLanguages.Python, 10000), default);
        if (r1.Symbols.Count == 0 || r2.Symbols.Count == 0) return;
        var all = r1.Symbols.Concat(r2.Symbols).ToList();
        var catalog = all.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new PythonRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("a.py", py1), new SourceFile("b.py", py2) }));
        var caller = all.Single(s => s.Name == "caller");
        var helper = all.Single(s => s.Name == "helper");
        rels.Should().Contain(r => r.FromSymbolId == caller.Id && r.ToSymbolId == helper.Id && r.RelationType == RelationType.Calls);
    }

    [Fact]
    public async Task Python_IsRelevantPath_Filters()
    {
        var cat = new PythonRelationExtractor(new InMemoryCatalog(Array.Empty<CatalogSymbol>()));
        cat.IsRelevantPath("src/app.py").Should().BeTrue();
        cat.IsRelevantPath("src/app.js").Should().BeFalse();
        cat.IsRelevantPath("bin/app.py").Should().BeFalse();
        cat.IsRelevantPath("obj/debug/app.py").Should().BeFalse();
        cat.Language.Should().Be(SupportedLanguages.Python);
    }
}
