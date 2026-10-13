using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using CodeRag.Documentation;
using CodeRag.Documentation.Models;
using CodeRag.Search;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CodeRag.Tests.Unit;

using Doc = CodeRag.Domain.Entities.Documentation;

/// <summary>
/// Orchestration of a single-target documentation run: freshness/skip, staleness/regenerate,
/// forced regeneration, and failure handling.
/// </summary>
public sealed class DocumentationServiceTests
{
    private readonly ISymbolStore _symbols = Substitute.For<ISymbolStore>();
    private readonly IFileStore _files = Substitute.For<IFileStore>();
    private readonly IDocumentationContextBuilder _contextBuilder = Substitute.For<IDocumentationContextBuilder>();
    private readonly IDocumentationGenerator _generator = Substitute.For<IDocumentationGenerator>();
    private readonly IDocumentationStore _store = Substitute.For<IDocumentationStore>();
    private readonly ITargetContentHasher _hasher = Substitute.For<ITargetContentHasher>();

    private readonly DocumentationService _service;

    public DocumentationServiceTests()
    {
        _service = new DocumentationService(
            _contextBuilder, _generator, _store, _hasher, _symbols, _files,
            NullLogger<DocumentationService>.Instance);

        _generator.IsAvailable.Returns(true);
        _contextBuilder.BuildAsync(Arg.Any<DocumentationTarget>(), Arg.Any<DocumentationBuildOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocumentationContext { Target = Target() }));
    }

    private static DocumentationTarget Target() =>
        new(DocumentationTargetType.Symbol, Guid.NewGuid(), "NS.Order", "Order", SymbolId: Guid.NewGuid());

    private static Doc Existing(string sourceHash) =>
        Doc.Create(Guid.NewGuid(), Guid.NewGuid(), DocumentationTargetType.Symbol, "NS.Order", "Order", sourceHash: sourceHash, content: "old");

    private static void VerifyUpsertCalled(IDocumentationStore store)
    {
        var calls = store.ReceivedCalls();
        calls.Any(c => c.GetMethodInfo().Name == "UpsertAsync").Should().BeTrue();
    }

    [Fact]
    public async Task Returns_not_configured_when_llm_unavailable()
    {
        _generator.IsAvailable.Returns(false);
        var repoId = Guid.NewGuid();

        var result = await _service.GenerateForTargetAsync(
            new DocumentationTarget(DocumentationTargetType.Symbol, repoId, "NS.Order", "Order"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("LLM not configured");
        await _generator.DidNotReceive().GenerateAsync(Arg.Any<DocumentationContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fresh_existing_is_returned_without_generating()
    {
        var repoId = Guid.NewGuid();
        var targetRef = "NS.Order";
        var existing = Existing(sourceHash: "current");
        _store.GetByTargetAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<CancellationToken>()).Returns(existing);
        _hasher.ComputeAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("current"));

        var result = await _service.GenerateForTargetAsync(
            new DocumentationTarget(DocumentationTargetType.Symbol, repoId, targetRef, "Order"));

        result.Documentation.Should().BeSameAs(existing);
        result.Generated.Should().BeFalse();
        result.Succeeded.Should().BeTrue();
        await _generator.DidNotReceive().GenerateAsync(Arg.Any<DocumentationContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stale_existing_triggers_regeneration()
    {
        var repoId = Guid.NewGuid();
        var targetRef = "NS.Order";
        var existing = Existing(sourceHash: "oldhash");
        _store.GetByTargetAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<CancellationToken>()).Returns(existing);
        _hasher.ComputeAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("newhash"));
        _generator.GenerateAsync(Arg.Any<DocumentationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocumentationGenerationResult("# New", "gpt", 100, 10, "v1")));

        var result = await _service.GenerateForTargetAsync(
            new DocumentationTarget(DocumentationTargetType.Symbol, repoId, targetRef, "Order"));

        result.Generated.Should().BeTrue();
        result.Documentation!.Content.Should().Be("# New");
        result.Documentation.SourceHash.Should().Be("newhash");
        result.Documentation.Status.Should().Be(DocumentationStatus.Generated);
        VerifyUpsertCalled(_store);
    }

    [Fact]
    public async Task Force_regenerates_even_when_not_stale()
    {
        var repoId = Guid.NewGuid();
        var targetRef = "NS.Order";
        var existing = Existing(sourceHash: "current");
        _store.GetByTargetAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<CancellationToken>()).Returns(existing);
        _hasher.ComputeAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("current"));
        _generator.GenerateAsync(Arg.Any<DocumentationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DocumentationGenerationResult("# New", "gpt", 100, 10, "v1")));

        var result = await _service.GenerateForTargetAsync(
            new DocumentationTarget(DocumentationTargetType.Symbol, repoId, targetRef, "Order"), force: true);

        result.Generated.Should().BeTrue();
        await _generator.Received(1).GenerateAsync(Arg.Any<DocumentationContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generator_failure_marks_entry_failed()
    {
        var repoId = Guid.NewGuid();
        var targetRef = "NS.Order";
        _store.GetByTargetAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<CancellationToken>()).Returns((Doc?)null);
        _hasher.ComputeAsync(repoId, DocumentationTargetType.Symbol, targetRef, Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("h1"));
        _generator.GenerateAsync(Arg.Any<DocumentationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DocumentationGenerationResult>(new InvalidOperationException("LLM down")));

        var result = await _service.GenerateForTargetAsync(
            new DocumentationTarget(DocumentationTargetType.Symbol, repoId, targetRef, "Order"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("LLM down");
        result.Documentation!.Status.Should().Be(DocumentationStatus.Failed);
    }
}
