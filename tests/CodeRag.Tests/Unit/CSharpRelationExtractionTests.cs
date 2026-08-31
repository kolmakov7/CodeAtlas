using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.CSharp;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression test for the C# whole-repository relation extractor.
///
/// Builds a realistic symbol catalog from the real <see cref="CSharpAnalyzer"/>
/// (the same symbols/FQNs the DB would store), then runs the real
/// <see cref="RoslynRelationExtractor"/> and asserts the required structural relations.
/// </summary>
public sealed class CSharpRelationExtractionTests
{
    private const string Fixture = """
        namespace CoderagFixture
        {
            public interface IService
            {
                void Execute();
            }

            public interface IRepository
            {
                void Save();
            }

            public sealed class Service : IService
            {
                private readonly IRepository _repository;

                public Service(IRepository repository)
                {
                    _repository = repository;
                }

                public void Execute()
                {
                    _repository.Save();
                }
            }
        }
        """;

    private sealed class InMemoryCatalog : ISymbolCatalog
    {
        private readonly IReadOnlyList<CatalogSymbol> _symbols;
        public InMemoryCatalog(IReadOnlyList<CatalogSymbol> symbols) => _symbols = symbols;
        public Task<IReadOnlyList<CatalogSymbol>> GetSymbolsAsync(Guid repositoryId, string? language = null, CancellationToken ct = default)
            => Task.FromResult(_symbols);
    }

    [Fact]
    public async Task Extracts_Required_Relations()
    {
        var repoId = Guid.NewGuid();

        // Catalog mirrors what the DB would store after the C# analyzer runs on the same source.
        var analyzer = new CSharpAnalyzer();
        var analyzed = await analyzer.AnalyzeAsync(new FileContext(repoId, null, "C.cs", Fixture, SupportedLanguages.CSharp), CancellationToken.None);
        var symbols = analyzed.Symbols.Where(s => !string.IsNullOrWhiteSpace(s.Fqn)).ToList();
        symbols.Should().NotBeEmpty("the C# analyzer must index symbols from the fixture");

        var catalog = symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var idToFqn = symbols.ToDictionary(s => s.Id, s => s.Fqn!);

        var extractor = new RoslynRelationExtractor(new InMemoryCatalog(catalog));
        var relations = await extractor.ExtractAsync(
            new RelationExtractionContext(repoId, new[] { new SourceFile("C.cs", Fixture) }));

        relations.Should().NotBeEmpty("the extractor must produce relations for the fixture");

        // Locate the catalog symbols by identity (name + kind), independent of the exact FQN text.
        var service = symbols.Single(s => s.Name == "Service" && s.Kind == SymbolKind.Class);
        var iService = symbols.Single(s => s.Name == "IService" && s.Kind == SymbolKind.Interface);
        var iRepo = symbols.Single(s => s.Name == "IRepository" && s.Kind == SymbolKind.Interface);
        var execute = symbols.Single(s => s.Kind == SymbolKind.Method && s.Fqn!.StartsWith(service.Fqn! + "."));
        var save = symbols.Single(s => s.Kind == SymbolKind.Method && s.Fqn!.StartsWith(iRepo.Fqn! + "."));

        bool Has(Guid from, Guid to, string type) =>
            relations.Any(r => r.FromSymbolId == from && r.ToSymbolId == to && r.RelationType == type);

        // 1. Service implements IService
        Has(service.Id, iService.Id, RelationType.Implements)
            .Should().BeTrue($"expected {service.Fqn} ->implements-> {iService.Fqn}; got: {Describe(relations, idToFqn)}");

        // 2. Service contains Execute
        Has(service.Id, execute.Id, RelationType.Contains)
            .Should().BeTrue($"expected {service.Fqn} ->contains-> {execute.Fqn}; got: {Describe(relations, idToFqn)}");

        // 3. Service depends_on IRepository
        Has(service.Id, iRepo.Id, RelationType.DependsOn)
            .Should().BeTrue($"expected {service.Fqn} ->depends_on-> {iRepo.Fqn}; got: {Describe(relations, idToFqn)}");

        // 4. Execute calls IRepository.Save
        Has(execute.Id, save.Id, RelationType.Calls)
            .Should().BeTrue($"expected {execute.Fqn} ->calls-> {save.Fqn}; got: {Describe(relations, idToFqn)}");

        // Reverse direction: the same edge is queryable from the callee side (to -> from).
        relations.Should().Contain(r => r.FromSymbolId == execute.Id && r.ToSymbolId == save.Id && r.RelationType == RelationType.Calls);
        relations.Should().Contain(r => r.ToSymbolId == save.Id && r.FromSymbolId == execute.Id);
    }

    private static string Describe(IReadOnlyList<Relation> relations, Dictionary<Guid, string> idToFqn)
    {
        string Fqn(Guid id) => idToFqn.TryGetValue(id, out var f) ? f : id.ToString();
        return string.Join("; ", relations.Select(r => $"{Fqn(r.FromSymbolId)} [{r.RelationType}] {Fqn(r.ToSymbolId)}"));
    }
}
