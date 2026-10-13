using CodeRag.Documentation.Models;
using CodeRag.Documentation.Prompts;
using CodeRag.Domain.Enums;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Prompt construction (versioned, English contract, per-type sections) and canonical
/// target-identity helpers (BuildTargetRef / NormalizePath).
/// </summary>
public sealed class DocumentationPromptsAndTargetTests
{
    private static DocumentationTarget Target(DocumentationTargetType type, string name) =>
        new(type, Guid.NewGuid(), DocumentationTarget.BuildTargetRef(type, null, name, null, null), name);

    [Fact]
    public void SystemPrompt_is_english_and_targets_the_named_element()
    {
        var system = DocumentationPrompts.BuildSystemPrompt(Target(DocumentationTargetType.Symbol, "OrderService"));

        system.Should().Contain("English");
        system.Should().Contain("OrderService");
        system.Should().Contain("# {name}".Replace("{name}", "OrderService"));
        // Anti-hallucination rule.
        system.Should().Contain("Do NOT invent");
    }

    [Fact]
    public void SystemPrompt_has_type_specific_sections()
    {
        DocumentationPrompts.BuildSystemPrompt(Target(DocumentationTargetType.Repository, "myrepo"))
            .Should().Contain("Architecture");
        DocumentationPrompts.BuildSystemPrompt(Target(DocumentationTargetType.Module, "src/core"))
            .Should().Contain("Key Components");
        DocumentationPrompts.BuildSystemPrompt(Target(DocumentationTargetType.Symbol, "Foo"))
            .Should().Contain("How It Works");
    }

    [Fact]
    public void UserPrompt_wraps_the_context_and_names_the_target()
    {
        var user = DocumentationPrompts.BuildUserPrompt(Target(DocumentationTargetType.Symbol, "Foo"), "TARGET: Foo\n...context...");

        user.Should().Contain("symbol \"Foo\"");
        user.Should().Contain("<context>");
        user.Should().Contain("TARGET: Foo");
    }

    [Fact]
    public void Prompt_version_is_stable()
    {
        DocumentationPrompts.CurrentVersion.Should().Be("v1");
    }

    [Theory]
    [InlineData(DocumentationTargetType.Repository, "repository")]
    public void BuildTargetRef_repository_is_constant(DocumentationTargetType type, string expected)
    {
        DocumentationTarget.BuildTargetRef(type, fqn: null, name: null, path: null, module: null)
            .Should().Be(expected);
    }

    [Fact]
    public void BuildTargetRef_symbol_prefers_fqn_then_name()
    {
        DocumentationTarget.BuildTargetRef(DocumentationTargetType.Symbol, "NS.OrderService", "OrderService", null, null)
            .Should().Be("NS.OrderService");
        DocumentationTarget.BuildTargetRef(DocumentationTargetType.Symbol, null, "OrderService", null, null)
            .Should().Be("OrderService");
        DocumentationTarget.BuildTargetRef(DocumentationTargetType.Symbol, null, null, null, null)
            .Should().Be("symbol");
    }

    [Theory]
    [InlineData("src\\core\\svc", "src/core/svc")]
    [InlineData("/src/core/", "src/core")]
    [InlineData("  ./src  ", "src")]
    [InlineData("", "root")]
    [InlineData(null, "root")]
    public void NormalizePath_normalizes_module_paths(string? input, string expected)
    {
        DocumentationTarget.NormalizePath(input).Should().Be(expected);
    }
}
