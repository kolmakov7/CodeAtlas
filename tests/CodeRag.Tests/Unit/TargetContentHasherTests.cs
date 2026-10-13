using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation;
using FluentAssertions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Content-fingerprint stability for staleness: identical source → identical hash;
/// a change to the target file or a direct dependency → different hash; no source → null.
/// </summary>
public sealed class TargetContentHasherTests
{
    private readonly IFileStore _files = Substitute.For<IFileStore>();
    private readonly ISymbolStore _symbols = Substitute.For<ISymbolStore>();
    private readonly IRelationStore _relations = Substitute.For<IRelationStore>();
    private readonly TargetContentHasher _hasher;

    public TargetContentHasherTests()
        => _hasher = new TargetContentHasher(_files, _symbols, _relations);

    private static FileEntry File(Guid id, Guid repo, string path, string? hash) =>
        FileEntry.Create(id, repo, path, hash: hash);

    private static Symbol Sym(Guid id, Guid repo, Guid file, string name) =>
        Symbol.Create(id, repo, file, "csharp", SymbolKind.Class, name);

    [Fact]
    public async Task Symbol_hash_is_deterministic_for_same_source()
    {
        var repo = Guid.NewGuid();
        var a = Guid.NewGuid();
        var fileA = Guid.NewGuid();

        _files.GetByIdAsync(fileA, Arg.Any<CancellationToken>()).Returns(File(fileA, repo, "src/A.cs", "h1"));
        _symbols.GetByIdAsync(a, Arg.Any<CancellationToken>()).Returns(Sym(a, repo, fileA, "A"));
        _relations.GetBySymbolAsync(a, RelationDirection.Outgoing, repo, Arg.Any<IReadOnlyList<RelationType>?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Relation>());

        var h1 = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");
        var h2 = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");

        h1.Should().NotBeNullOrEmpty();
        h1.Should().Be(h2);
    }

    [Fact]
    public async Task Changing_the_target_file_changes_the_hash()
    {
        var repo = Guid.NewGuid();
        var a = Guid.NewGuid();
        var fileA = Guid.NewGuid();
        _symbols.GetByIdAsync(a, Arg.Any<CancellationToken>()).Returns(Sym(a, repo, fileA, "A"));
        _relations.GetBySymbolAsync(a, RelationDirection.Outgoing, repo, Arg.Any<IReadOnlyList<RelationType>?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Relation>());
        _files.GetByIdAsync(fileA, Arg.Any<CancellationToken>()).Returns(File(fileA, repo, "src/A.cs", "h1"));

        var before = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");

        _files.GetByIdAsync(fileA, Arg.Any<CancellationToken>()).Returns(File(fileA, repo, "src/A.cs", "h2"));
        var after = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");

        before.Should().NotBeNullOrEmpty();
        after.Should().NotBe(before);
    }

    [Fact]
    public async Task Changing_a_direct_dependency_changes_the_hash()
    {
        var repo = Guid.NewGuid();
        var a = Guid.NewGuid(); // target
        var b = Guid.NewGuid(); // dependency
        var fileA = Guid.NewGuid();
        var fileB = Guid.NewGuid();

        _files.GetByIdAsync(fileA, Arg.Any<CancellationToken>()).Returns(File(fileA, repo, "src/A.cs", "ha"));
        _symbols.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<Guid>(0);
                return Task.FromResult<Symbol?>(
                    id == a ? Sym(a, repo, fileA, "A") : Sym(b, repo, fileB, "B"));
            });
        _relations.GetBySymbolAsync(a, RelationDirection.Outgoing, repo, Arg.Any<IReadOnlyList<RelationType>?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new Relation(a, b, RelationType.References) });

        _files.GetByIdAsync(fileB, Arg.Any<CancellationToken>()).Returns(File(fileB, repo, "src/B.cs", "hb1"));
        var before = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");

        _files.GetByIdAsync(fileB, Arg.Any<CancellationToken>()).Returns(File(fileB, repo, "src/B.cs", "hb2"));
        var after = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");

        before.Should().NotBeNullOrEmpty();
        after.Should().NotBe(before);
    }

    [Fact]
    public async Task Returns_null_when_no_source_is_resolvable()
    {
        var repo = Guid.NewGuid();
        var a = Guid.NewGuid();
        var fileA = Guid.NewGuid();
        _files.GetByIdAsync(fileA, Arg.Any<CancellationToken>()).Returns(File(fileA, repo, "src/A.cs", hash: null));
        _symbols.GetByIdAsync(a, Arg.Any<CancellationToken>()).Returns(Sym(a, repo, fileA, "A"));
        _relations.GetBySymbolAsync(a, RelationDirection.Outgoing, repo, Arg.Any<IReadOnlyList<RelationType>?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Relation>());

        var hash = await _hasher.ComputeAsync(repo, DocumentationTargetType.Symbol, "A", a, "src/A.cs");
        hash.Should().BeNull();
    }

    [Fact]
    public async Task Repository_hash_covers_all_files()
    {
        var repo = Guid.NewGuid();
        var f1 = Guid.NewGuid();
        var f2 = Guid.NewGuid();
        _files.GetByRepositoryAsync(repo, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                File(f1, repo, "src/A.cs", "a"),
                File(f2, repo, "src/B.cs", "b"),
            });

        var hash = await _hasher.ComputeAsync(repo, DocumentationTargetType.Repository, "repository", null, null);
        hash.Should().NotBeNullOrEmpty();
    }
}
