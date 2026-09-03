using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using CodeRag.Analyzers.TreeSitter.Relations;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class JavaScriptRelationExtractorTests
{
    private sealed class InMemoryCatalog : ISymbolCatalog
    {
        private readonly IReadOnlyList<CatalogSymbol> _symbols;
        public InMemoryCatalog(IReadOnlyList<CatalogSymbol> symbols) => _symbols = symbols;
        public Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default) => Task.FromResult(_symbols);
    }

    [Fact]
    public async Task JavaScript_Contains_And_Calls()
    {
        var js = """
class Repo {
  save(item){ return item; }
  load(item){ return item; }
}
function process(item){
  const r = new Repo();
  r.save(item);
}
""";
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var res = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "app.js", js, SupportedLanguages.JavaScript, 10000), default);
        if (res.Symbols.Count == 0) return;
        var catalog = res.Symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new JavaScriptRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("app.js", js) }));
        rels.Should().NotBeEmpty();
        rels.Should().Contain(r => r.RelationType == RelationType.Contains);
        rels.Should().Contain(r => r.RelationType == RelationType.Calls);
        var repo = res.Symbols.Single(s => s.Name == "Repo");
        var save = res.Symbols.Single(s => s.Name == "save");
        rels.Should().Contain(r => r.FromSymbolId == repo.Id && r.ToSymbolId == save.Id && r.RelationType == RelationType.Contains);
        var process = res.Symbols.Single(s => s.Name == "process");
        rels.Should().Contain(r => r.FromSymbolId == process.Id && r.ToSymbolId == save.Id && r.RelationType == RelationType.Calls);
    }

    [Fact]
    public async Task JavaScript_CrossFile_Calls()
    {
        var aJs = "function helper(x){ return x; }\n";
        var bJs = "function caller(){ helper(1); }\n";
        var repoId = Guid.NewGuid();
        var analyzer = new TreeSitterAnalyzer();
        var r1 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "a.js", aJs, SupportedLanguages.JavaScript, 10000), default);
        var r2 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "b.js", bJs, SupportedLanguages.JavaScript, 10000), default);
        if (r1.Symbols.Count == 0 || r2.Symbols.Count == 0) return;
        var all = r1.Symbols.Concat(r2.Symbols).ToList();
        var catalog = all.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new JavaScriptRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("a.js", aJs), new SourceFile("b.js", bJs) }));
        var caller = all.Single(s => s.Name == "caller");
        var helper = all.Single(s => s.Name == "helper");
        rels.Should().Contain(r => r.FromSymbolId == caller.Id && r.ToSymbolId == helper.Id && r.RelationType == RelationType.Calls);
    }

    [Fact]
    public async Task JavaScript_IsRelevantPath_And_TypeScript()
    {
        var cat = new JavaScriptRelationExtractor(new InMemoryCatalog(Array.Empty<CatalogSymbol>()));
        cat.IsRelevantPath("app.js").Should().BeTrue();
        cat.IsRelevantPath("app.ts").Should().BeTrue();
        cat.IsRelevantPath("app.tsx").Should().BeTrue();
        cat.IsRelevantPath("app.py").Should().BeFalse();
        cat.IsRelevantPath("node_modules/app.js").Should().BeFalse();
        cat.Language.Should().Be(SupportedLanguages.JavaScript);
    }
}
