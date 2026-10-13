using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;
using FluentAssertions;
using Doc = CodeRag.Domain.Entities.Documentation;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Entity invariants and staleness semantics for a generated documentation entry.
/// </summary>
public sealed class DocumentationEntityTests
{
    private static Doc NewDoc(string? sourceHash = "abc") =>
        Doc.Create(
            Guid.NewGuid(), Guid.NewGuid(), DocumentationTargetType.Symbol,
            "NS.OrderService", "OrderService", sourceHash: sourceHash);

    [Fact]
    public void IsStaleAgainst_same_hash_is_false()
    {
        NewDoc("abc").IsStaleAgainst("abc").Should().BeFalse();
    }

    [Fact]
    public void IsStaleAgainst_different_hash_is_true()
    {
        NewDoc("abc").IsStaleAgainst("def").Should().BeTrue();
    }

    [Fact]
    public void IsStaleAgainst_missing_hash_is_false()
    {
        NewDoc(sourceHash: null).IsStaleAgainst("def").Should().BeFalse();
        NewDoc("abc").IsStaleAgainst(null).Should().BeFalse();
    }

    [Fact]
    public void MarkStale_sets_status_and_reason()
    {
        var doc = NewDoc();
        doc.MarkStale("dependency changed");

        doc.Status.Should().Be(DocumentationStatus.Stale);
        doc.Error.Should().Be("dependency changed");
    }

    [Fact]
    public void MarkFailed_sets_status_and_error()
    {
        var doc = NewDoc();
        doc.MarkFailed("boom");

        doc.Status.Should().Be(DocumentationStatus.Failed);
        doc.Error.Should().Be("boom");
    }

    [Fact]
    public void MarkGenerating_sets_status()
    {
        var doc = NewDoc();
        doc.MarkGenerating();

        doc.Status.Should().Be(DocumentationStatus.Generating);
    }

    [Fact]
    public void UpdateContent_replaces_content_and_clears_error()
    {
        var doc = NewDoc();
        doc.MarkFailed("boom");
        var before = doc.UpdatedAt;

        doc.UpdateContent("# New", model: "gpt-x", promptVersion: "v1", sourceHash: "newhash",
            contextChars: 1234, filePath: "src/A.cs");

        doc.Content.Should().Be("# New");
        doc.Status.Should().Be(DocumentationStatus.Generated);
        doc.Error.Should().BeNull();
        doc.SourceHash.Should().Be("newhash");
        doc.OutputChars.Should().Be("# New".Length);
        doc.ContextChars.Should().Be(1234);
        doc.FilePath.Should().Be("src/A.cs");
        doc.UpdatedAt.Should().BeAfter(before.AddTicks(-1)); // advanced (same tick tolerated via >=)
    }

    [Fact]
    public void UpdateContent_throws_on_empty_content()
    {
        var act = () => NewDoc().UpdateContent("   ", null, "v1", null);
        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_enforces_required_fields()
    {
        // Guard rails: empty id / repository / targetRef / name all throw.
        Action noId = () => Doc.Create(Guid.Empty, Guid.NewGuid(), DocumentationTargetType.Symbol, "r", "n");
        Action noRepo = () => Doc.Create(Guid.NewGuid(), Guid.Empty, DocumentationTargetType.Symbol, "r", "n");
        Action noRef = () => Doc.Create(Guid.NewGuid(), Guid.NewGuid(), DocumentationTargetType.Symbol, "  ", "n");
        Action noName = () => Doc.Create(Guid.NewGuid(), Guid.NewGuid(), DocumentationTargetType.Symbol, "r", "   ");

        noId.Should().Throw<DomainValidationException>();
        noRepo.Should().Throw<DomainValidationException>();
        noRef.Should().Throw<DomainValidationException>();
        noName.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Create_trims_ref_and_name()
    {
        var doc = Doc.Create(Guid.NewGuid(), Guid.NewGuid(), DocumentationTargetType.Symbol,
            "  NS.Foo  ", "  Foo  ");

        doc.TargetRef.Should().Be("NS.Foo");
        doc.Name.Should().Be("Foo");
    }
}
