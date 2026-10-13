using CodeRag.Documentation;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Post-processing of raw LLM output into well-formed Markdown
/// (fence stripping, preamble removal, sign-off removal, blank-line collapse).
/// </summary>
public sealed class DocumentationValidatorTests
{
    [Fact]
    public void Normalize_strips_wrapping_markdown_fence()
    {
        var raw = "```markdown\n# Service\n\nResponsible for X.\n```";
        var result = DocumentationValidator.Normalize(raw);

        result.Should().Be("# Service\n\nResponsible for X.");
        DocumentationValidator.LooksLikeMarkdown(result).Should().BeTrue();
    }

    [Fact]
    public void Normalize_drops_preamble_before_first_heading()
    {
        var raw = "Here is the documentation you asked for:\n# Service\n\nResponsible for X.";
        var result = DocumentationValidator.Normalize(raw);

        result.Should().StartWith("# Service");
        result.Should().NotContain("Here is the documentation");
    }

    [Fact]
    public void Normalize_strips_trailing_sign_off()
    {
        var raw = "# Service\n\nResponsible for X.\n\nHope this helps!";
        var result = DocumentationValidator.Normalize(raw);

        result.Should().NotContain("Hope this helps");
        result.Should().EndWith("Responsible for X.");
    }

    [Fact]
    public void Normalize_collapses_excess_blank_lines()
    {
        var raw = "# Service\n\n\n\n\nBody text.";
        var result = DocumentationValidator.Normalize(raw);

        result.Should().NotContain("\n\n\n");
        result.Should().Be("# Service\n\nBody text.");
    }

    [Fact]
    public void Normalize_returns_empty_for_blank_input()
    {
        DocumentationValidator.Normalize("   \n  ").Should().BeEmpty();
        DocumentationValidator.Normalize(null!).Should().BeEmpty();
    }

    [Theory]
    [InlineData("# Title\nbody", true)]
    [InlineData("## Heading\nmore", true)]
    [InlineData("just prose, no heading", false)]
    [InlineData("", false)]
    public void LooksLikeMarkdown_detects_a_heading(string doc, bool expected)
    {
        DocumentationValidator.LooksLikeMarkdown(doc).Should().Be(expected);
    }
}
