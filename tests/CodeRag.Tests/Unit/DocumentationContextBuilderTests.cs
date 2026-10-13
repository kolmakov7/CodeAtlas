using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation;
using CodeRag.Documentation.Models;
using CodeRag.Search;
using CodeRag.Search.Models;
using FluentAssertions;
using NSubstitute;
using DomainSymbol = CodeRag.Domain.Entities.Symbol;
using SearchSymbol = CodeRag.Search.Models.Symbol;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Context assembly strictly from the index: the symbol path includes the target source and
/// categorizes one-hop relations; the repository path includes overview, key components and
/// search-derived snippets.
/// </summary>
public sealed class DocumentationContextBuilderTests
{
    private readonly ISymbolStore _symbols = Substitute.For<ISymbolStore>();
    private readonly IFileStore _files = Substitute.For<IFileStore>();
    private readonly IRelationStore _relations = Substitute.For<IRelationStore>();
    private readonly IRepositoryStore _repos = Substitute.For<IRepositoryStore>();
    private readonly ISearchService _search = Substitute.For<ISearchService>();
    private readonly DocumentationContextBuilder _builder;

    public DocumentationContextBuilderTests()
        => _builder = new DocumentationContextBuilder(_symbols, _files, _relations, _repos, _search);

    private static SearchResult EmptySearch() =>
        new(Array.Empty<SearchResultItem>(), 0, Array.Empty<string>());

    [Fact]
    public async Task Symbol_context_includes_target_source_and_categorizes_relations()
    {
        var repo = Guid.NewGuid();
        var symA = Guid.NewGuid();
        var depId = Guid.NewGuid();
        var fileA = Guid.NewGuid();
        var fileB = Guid.NewGuid();

        _symbols.GetByIdAsync(symA, Arg.Any<CancellationToken>())
            .Returns(DomainSymbol.Create(symA, repo, fileA, "csharp", SymbolKind.Class, "A", fqn: "NS.A", startLine: 1, endLine: 10));
        _files.GetByIdAsync(fileA, Arg.Any<CancellationToken>())
            .Returns(FileEntry.Create(fileA, repo, "src/A.cs", hash: "h"));
        _search.GetCodeAsync(Arg.Any<GetCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SourceSlice(repo, fileA, "src/A.cs", "csharp", 1, 10, 10, "class A { }", "class A")));
        _search.GetRelationsAsync(symA, RelationDirection.Both, null, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new RelationEntry(symA, depId, "References", null,
                    new SearchSymbol(depId, repo, fileB, "csharp", "class", "B", "NS.B", null, null, 1, 5, "src/B.cs"))
            });
        _search.SearchCodeAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptySearch()));

        var target = new DocumentationTarget(DocumentationTargetType.Symbol, repo, "NS.A", "A", SymbolId: symA);
        var ctx = await _builder.BuildAsync(target);

        ctx.FilePath.Should().Be("src/A.cs");
        ctx.Sources.Should().Contain(s => s.Label == "Target source" && s.Code.Contains("class A { }"));
        ctx.Dependencies.Should().Contain(d => d.Name == "B");
        ctx.BaseTypes.Should().BeEmpty();
        ctx.Callers.Should().BeEmpty();
        ctx.SourceMissing.Should().BeFalse();
        ctx.ToPromptText().Should().Contain("DEPENDS ON");
    }

    [Fact]
    public async Task Symbol_context_reports_warning_when_symbol_unresolvable()
    {
        var repo = Guid.NewGuid();
        var symA = Guid.NewGuid();
        _symbols.GetByIdAsync(symA, Arg.Any<CancellationToken>()).Returns((DomainSymbol?)null);

        var target = new DocumentationTarget(DocumentationTargetType.Symbol, repo, "NS.A", "A", SymbolId: symA);
        var ctx = await _builder.BuildAsync(target);

        ctx.SourceMissing.Should().BeTrue();
        ctx.Warnings.Should().Contain("could not be resolved");
    }

    [Fact]
    public async Task Repository_context_includes_overview_key_components_and_snippets()
    {
        var repo = Guid.NewGuid();
        var f1 = Guid.NewGuid();
        var symA = Guid.NewGuid();

        _repos.GetByIdAsync(repo, Arg.Any<CancellationToken>())
            .Returns(Repository.Create(repo, "demo", "https://github.com/acme/demo.git", "main", "/data/repos/demo"));
        _files.GetByRepositoryAsync(repo, Arg.Any<CancellationToken>())
            .Returns(new[] { FileEntry.Create(f1, repo, "src/A.cs", hash: "h", language: "csharp") });
        _symbols.GetByRepositoryAsync(repo, Arg.Any<CancellationToken>())
            .Returns(new[] { DomainSymbol.Create(symA, repo, f1, "csharp", SymbolKind.Class, "A", fqn: "NS.A") });
        _relations.GetByRepositoryAsync(repo, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Relation>());
        _search.GetCodeAsync(Arg.Any<GetCodeRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SourceSlice(repo, f1, "src/A.cs", "csharp", 1, 5, 5, "class A { }", "class A")));

        var target = new DocumentationTarget(DocumentationTargetType.Repository, repo, "repository", "demo", RepositoryName: "demo");
        var ctx = await _builder.BuildAsync(target);

        ctx.RepositoryName.Should().Be("demo");
        ctx.Members.Should().Contain(m => m.Name == "A");
        ctx.Sources.Should().Contain(s => s.Label == "Source sample");
        ctx.ToPromptText().Should().Contain("demo");
    }
}
