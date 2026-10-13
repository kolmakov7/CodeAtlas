using CodeRag.Documentation;
using CodeRag.Documentation.Models;
using CodeRag.Domain.Enums;
using CodeRag.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// The LLM-backed generator: builds the English prompt from context, normalizes output,
/// and degrades gracefully when the endpoint is unconfigured.
/// </summary>
public sealed class LlmDocumentationGeneratorTests
{
    private static DocumentationContext Context()
    {
        var target = new DocumentationTarget(
            DocumentationTargetType.Symbol, Guid.NewGuid(), "NS.Order", "Order");
        return new DocumentationContext
        {
            Target = target,
            RepositoryName = "demo",
            Sources = { new ContextSource("Target source", "csharp", "src/Order.cs", 1, 10, "class Order { }") }
        };
    }

    private static IOptionsMonitor<LlmOptions> Options()
    {
        var monitor = Substitute.For<IOptionsMonitor<LlmOptions>>();
        monitor.CurrentValue.Returns(new LlmOptions { Endpoint = "http://llm/v1", Model = "gpt-test" });
        return monitor;
    }

    [Fact]
    public void IsAvailable_reflects_client_configuration()
    {
        var configured = Substitute.For<IChatCompletionClient>();
        configured.IsConfigured.Returns(true);
        new LlmDocumentationGenerator(configured, Options(), NullLogger<LlmDocumentationGenerator>.Instance).IsAvailable.Should().BeTrue();

        var unconfigured = Substitute.For<IChatCompletionClient>();
        unconfigured.IsConfigured.Returns(false);
        new LlmDocumentationGenerator(unconfigured, Options(), NullLogger<LlmDocumentationGenerator>.Instance).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_builds_english_prompt_and_returns_normalized_markdown()
    {
        var client = Substitute.For<IChatCompletionClient>();
        client.IsConfigured.Returns(true);
        ChatCompletionRequest? captured = null;
        client.CompleteAsync(Arg.Do<ChatCompletionRequest>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatCompletionResult("```markdown\n# Order\n\nDoes things.\n```", "gpt-test")));

        var gen = new LlmDocumentationGenerator(client, Options(), NullLogger<LlmDocumentationGenerator>.Instance);
        var result = await gen.GenerateAsync(Context());

        result.Content.Should().Be("# Order\n\nDoes things.");
        result.Model.Should().Be("gpt-test");
        result.PromptVersion.Should().Be("v1");

        captured.Should().NotBeNull();
        captured!.SystemPrompt.Should().Contain("English");
        captured.UserPrompt.Should().Contain("class Order { }");
        captured.UserPrompt.Should().Contain("symbol \"Order\"");
    }

    [Fact]
    public async Task GenerateAsync_throws_when_unconfigured()
    {
        var client = Substitute.For<IChatCompletionClient>();
        client.IsConfigured.Returns(false);
        var gen = new LlmDocumentationGenerator(client, Options(), NullLogger<LlmDocumentationGenerator>.Instance);

        Func<Task> act = () => gen.GenerateAsync(Context());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateAsync_keeps_raw_output_when_not_markdown()
    {
        var client = Substitute.For<IChatCompletionClient>();
        client.IsConfigured.Returns(true);
        client.CompleteAsync(Arg.Any<ChatCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatCompletionResult("plain text without any heading", "gpt-test")));

        var gen = new LlmDocumentationGenerator(client, Options(), NullLogger<LlmDocumentationGenerator>.Instance);
        var result = await gen.GenerateAsync(Context());

        result.Content.Should().Be("plain text without any heading");
    }
}
