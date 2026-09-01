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

    private static async Task<(List<Symbol> Symbols, List<Relation> Relations, Dictionary<Guid, string> IdToFqn)> RunAsync(string code, string path = "C.cs")
    {
        var repoId = Guid.NewGuid();
        var analyzer = new CSharpAnalyzer();
        var analyzed = await analyzer.AnalyzeAsync(new FileContext(repoId, null, path, code, SupportedLanguages.CSharp), CancellationToken.None);
        var symbols = analyzed.Symbols.Where(s => !string.IsNullOrWhiteSpace(s.Fqn)).ToList();

        var catalog = symbols.Select(s => new CatalogSymbol(s.Id, s.Fqn, s.Name)).ToList();
        var extractor = new RoslynRelationExtractor(new InMemoryCatalog(catalog));
        var relations = (await extractor.ExtractAsync(
            new RelationExtractionContext(repoId, new[] { new SourceFile(path, code) }))).ToList();

        return (symbols, relations, symbols.ToDictionary(s => s.Id, s => s.Fqn!));
    }

    /// <summary>
    /// F1 regression: member symbols (property/field) must carry a QUALIFIED FQN
    /// (`ContainingTypeFqn.MemberName`), not the bare member name. FullyQualifiedFormat
    /// renders member symbols as bare names, so they are built from the containing type.
    /// </summary>
    [Fact]
    public async Task Member_Symbols_Have_Qualified_Fqns()
    {
        var code = """
            namespace CoderagFixture
            {
                public class Order
                {
                    public string Name { get; set; }
                    private int _count;
                }
            }
            """;
        var (symbols, _, _) = await RunAsync(code);

        var order = symbols.Single(s => s.Name == "Order" && s.Kind == SymbolKind.Class);
        var name = symbols.Single(s => s.Name == "Name" && s.Kind == SymbolKind.Property);
        var count = symbols.Single(s => s.Name == "_count" && s.Kind == SymbolKind.Field);

        name.Fqn.Should().Be($"{order.Fqn}.Name")
            .And.NotBe("Name", "a property FQN must be qualified by its containing type");
        count.Fqn.Should().Be($"{order.Fqn}._count")
            .And.NotBe("_count", "a field FQN must be qualified by its containing type");
    }

    /// <summary>
    /// F2 regression: an unresolved external type (e.g. a BCL type outside the reference
    /// closure) renders as a bare name. It must NOT collide with a same-named catalog member
    /// and produce bogus DependsOn/References edges.
    /// </summary>
    [Fact]
    public async Task Unresolved_Type_Does_Not_Collide_With_Same_Named_Member()
    {
        var code = """
            namespace CoderagFixture
            {
                public class Api
                {
                    // `Created` is unresolved here (not in the compilation references), so it
                    // is an error type that displays as the bare name "Created".
                    public Created GetCreated()
                    {
                        return default;
                    }
                }

                public class ViewModel
                {
                    public DateTime Created { get; set; }
                }
            }
            """;
        var (symbols, relations, idToFqn) = await RunAsync(code);

        var api = symbols.Single(s => s.Name == "Api" && s.Kind == SymbolKind.Class);
        var createdProp = symbols.Single(s => s.Name == "Created" && s.Kind == SymbolKind.Property);

        // The legitimate structural edge (ViewModel contains the property) is fine; what must not
        // appear is a semantic edge (DependsOn/References/...) caused by the bare-name collision.
        relations.Should().NotContain(
            r => r.ToSymbolId == createdProp.Id && r.RelationType != RelationType.Contains,
            "an unresolved bare-named type must not attract semantic edges; got: " + Describe(relations, idToFqn));
        relations.Should().NotContain(r => r.FromSymbolId == api.Id && r.ToSymbolId == createdProp.Id);
    }

    /// <summary>
    /// F3 regression: the extractor must emit `References` edges (any semantic usage:
    /// parameter/field return types, field reads, call sites, type-head base lists), not only
    /// the narrow structural types.
    /// </summary>
    [Fact]
    public async Task References_Are_Emitted_For_Types_Fields_And_Calls()
    {
        var code = """
            namespace CoderagFixture
            {
                public interface IRepo
                {
                    void Save();
                }

                public class Item
                {
                    public string Name { get; set; }
                }

                public class OrderService : IRepo
                {
                    private readonly IRepo _repo;

                    public OrderService(IRepo repo)
                    {
                        _repo = repo;
                    }

                    public void Process(Item item)
                    {
                        _repo.Save();
                        var label = item.Name;
                    }
                }
            }
            """;
        var (symbols, relations, idToFqn) = await RunAsync(code);

        var iRepo = symbols.Single(s => s.Name == "IRepo" && s.Kind == SymbolKind.Interface);
        var save = symbols.Single(s => s.Kind == SymbolKind.Method && s.Fqn!.StartsWith(iRepo.Fqn! + "."));
        var item = symbols.Single(s => s.Name == "Item" && s.Kind == SymbolKind.Class);
        var nameProp = symbols.Single(s => s.Kind == SymbolKind.Property && s.Fqn!.StartsWith(item.Fqn! + "."));
        var orderService = symbols.Single(s => s.Name == "OrderService" && s.Kind == SymbolKind.Class);
        var repoField = symbols.Single(s => s.Name == "_repo" && s.Kind == SymbolKind.Field);
        var process = symbols.Single(s => s.Kind == SymbolKind.Method && s.Fqn!.StartsWith(orderService.Fqn! + ".Process"));

        bool Has(Guid from, Guid to, string type) =>
            relations.Any(r => r.FromSymbolId == from && r.ToSymbolId == to && r.RelationType == type);

        // References must actually be present (regression: extractor used to emit none).
        relations.Any(r => r.RelationType == RelationType.References)
            .Should().BeTrue("the extractor must emit References edges; got: " + Describe(relations, idToFqn));

        // Process references the Item type (parameter type).
        Has(process.Id, item.Id, RelationType.References)
            .Should().BeTrue($"expected {idToFqn[process.Id]} ->references-> {idToFqn[item.Id]}; got: {Describe(relations, idToFqn)}");

        // Process references the Item.Name property (item.Name).
        Has(process.Id, nameProp.Id, RelationType.References)
            .Should().BeTrue($"expected {idToFqn[process.Id]} ->references-> {idToFqn[nameProp.Id]}; got: {Describe(relations, idToFqn)}");

        // Process references the _repo field (field read) and the IRepo.Save method (call site).
        Has(process.Id, repoField.Id, RelationType.References)
            .Should().BeTrue($"expected {idToFqn[process.Id]} ->references-> {idToFqn[repoField.Id]}; got: {Describe(relations, idToFqn)}");
        Has(process.Id, save.Id, RelationType.References)
            .Should().BeTrue($"expected {idToFqn[process.Id]} ->references-> {idToFqn[save.Id]}; got: {Describe(relations, idToFqn)}");

        // Type head: OrderService references IRepo (base list) in addition to Implements.
        Has(orderService.Id, iRepo.Id, RelationType.References)
            .Should().BeTrue($"expected {idToFqn[orderService.Id]} ->references-> {idToFqn[iRepo.Id]}; got: {Describe(relations, idToFqn)}");
        Has(orderService.Id, iRepo.Id, RelationType.Implements)
            .Should().BeTrue("Implements edge must be preserved alongside References");
    }

    private static string Describe(IReadOnlyList<Relation> relations, Dictionary<Guid, string> idToFqn)
    {
        string Fqn(Guid id) => idToFqn.TryGetValue(id, out var f) ? f : id.ToString();
        return string.Join("; ", relations.Select(r => $"{Fqn(r.FromSymbolId)} [{r.RelationType}] {Fqn(r.ToSymbolId)}"));
    }
}
