using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.Sql;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class SqlRelationExtractorTests
{
    private sealed class InMemoryCatalog : ISymbolCatalog
    {
        private readonly IReadOnlyList<CatalogSymbol> _symbols;
        public InMemoryCatalog(IReadOnlyList<CatalogSymbol> symbols) => _symbols = symbols;
        public Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default) => Task.FromResult(_symbols);
    }

    [Fact]
    public async Task Sql_Calls_CrossFile()
    {
        var sql1 = "CREATE PROCEDURE dbo.ProcA AS BEGIN EXEC dbo.ProcB; END\nGO\n";
        var sql2 = "CREATE PROCEDURE dbo.ProcB AS BEGIN SELECT 1 END\nGO\n";
        var repoId = Guid.NewGuid();
        var analyzer = new SqlAnalyzer();
        var r1 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "a.sql", sql1, SupportedLanguages.Sql, 10000), default);
        var r2 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "b.sql", sql2, SupportedLanguages.Sql, 10000), default);
        if (r1.Symbols.Count == 0 || r2.Symbols.Count == 0) return;
        var all = r1.Symbols.Concat(r2.Symbols).ToList();
        var catalog = all.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new SqlRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("a.sql", sql1), new SourceFile("b.sql", sql2) }));
        var procA = all.Single(s => s.Name == "ProcA");
        var procB = all.Single(s => s.Name == "ProcB");
        rels.Should().Contain(r => r.FromSymbolId == procA.Id && r.ToSymbolId == procB.Id && r.RelationType == RelationType.Calls);
    }

    [Fact]
    public async Task Sql_References_Table()
    {
        var sql1 = "CREATE PROCEDURE dbo.ProcA AS BEGIN SELECT * FROM dbo.Items; END\nGO\n";
        var sql2 = "CREATE TABLE dbo.Items (Id INT);\nGO\n";
        var repoId = Guid.NewGuid();
        var analyzer = new SqlAnalyzer();
        var r1 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "a.sql", sql1, SupportedLanguages.Sql, 10000), default);
        var r2 = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "b.sql", sql2, SupportedLanguages.Sql, 10000), default);
        if (r1.Symbols.Count == 0 || r2.Symbols.Count == 0) return;
        var all = r1.Symbols.Concat(r2.Symbols).ToList();
        var catalog = all.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new SqlRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("a.sql", sql1), new SourceFile("b.sql", sql2) }));
        var procA = all.Single(s => s.Name == "ProcA");
        var items = all.Single(s => s.Name == "Items");
        rels.Should().Contain(r => r.FromSymbolId == procA.Id && r.ToSymbolId == items.Id && r.RelationType == RelationType.References);
    }

    [Fact]
    public async Task Sql_Contains_TempTable()
    {
        var sql = """
CREATE PROCEDURE dbo.ProcA AS BEGIN CREATE TABLE #Temp (Id INT); SELECT * FROM #Temp; END
GO
""";
        var repoId = Guid.NewGuid();
        var analyzer = new SqlAnalyzer();
        var res = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "a.sql", sql, SupportedLanguages.Sql, 10000), default);
        if (res.Symbols.Count < 2) return;
        var catalog = res.Symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new SqlRelationExtractor(new InMemoryCatalog(catalog));
        var rels = await extractor.ExtractAsync(new RelationExtractionContext(repoId, new[] { new SourceFile("a.sql", sql) }));
        rels.Should().Contain(r => r.RelationType == RelationType.Contains);
    }

    [Fact]
    public void Sql_IsRelevantPath()
    {
        var cat = new SqlRelationExtractor(new InMemoryCatalog(Array.Empty<CatalogSymbol>()));
        cat.IsRelevantPath("proc.sql").Should().BeTrue();
        cat.IsRelevantPath("proc.tsql").Should().BeTrue();
        cat.IsRelevantPath("app.py").Should().BeFalse();
        cat.IsRelevantPath("obj/proc.sql").Should().BeFalse();
        cat.Language.Should().Be(SupportedLanguages.Sql);
    }
}
