using CodeRag.Api.Models;
using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Indexer;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Dual-embedding (code / text) architecture tests: domain enum, classification,
/// per-domain chunking, per-domain options, per-domain provider/dim factories,
/// and per-domain settings (flatten + config fallback).
/// </summary>
public sealed class DualEmbeddingTests
{
    // ---- EmbeddingDomain enum --------------------------------------------

    [Theory]
    [InlineData(EmbeddingDomain.Code, "code")]
    [InlineData(EmbeddingDomain.Text, "text")]
    public void toText_returns_stable_token(EmbeddingDomain domain, string expected)
        => domain.ToText().Should().Be(expected);

    [Theory]
    [InlineData("code", EmbeddingDomain.Code)]
    [InlineData("CODE", EmbeddingDomain.Code)]
    [InlineData("c#", EmbeddingDomain.Code)]   // unknown -> Code fallback (code search default)
    [InlineData("text", EmbeddingDomain.Text)]
    [InlineData("TEXT", EmbeddingDomain.Text)]
    [InlineData("prose", EmbeddingDomain.Text)]
    [InlineData("documentation", EmbeddingDomain.Text)]
    [InlineData("docs", EmbeddingDomain.Text)]
    [InlineData("", EmbeddingDomain.Code)]
    public void toDomain_parses_known_values(string value, EmbeddingDomain expected)
        => value.ToDomain().Should().Be(expected);

    [Fact]
    public void toDomain_null_falls_back_to_code()
        => ((string?)null).ToDomain().Should().Be(EmbeddingDomain.Code);

    // ---- ContentClassifier ------------------------------------------------

    [Theory]
    [InlineData("src/Foo.cs", "csharp", EmbeddingDomain.Code)]
    [InlineData("app/main.py", "python", EmbeddingDomain.Code)]
    [InlineData("web/app.js", "javascript", EmbeddingDomain.Code)]
    [InlineData("cmd/tool.go", "go", EmbeddingDomain.Code)]
    [InlineData("lib/thing.cpp", "cpp", EmbeddingDomain.Code)]
    [InlineData("db/query.sql", "sql", EmbeddingDomain.Code)]
    [InlineData("legacy/script.vbs", "vbs", EmbeddingDomain.Code)]
    [InlineData("docs/guide.mdx", "markdown", EmbeddingDomain.Text)]
    [InlineData("docs/notes.rst", "rst", EmbeddingDomain.Text)]
    [InlineData("docs/api.adoc", "adoc", EmbeddingDomain.Text)]
    [InlineData("README.md", "markdown", EmbeddingDomain.Text)]
    [InlineData("CHANGELOG", null, EmbeddingDomain.Text)]
    [InlineData("LICENSE.txt", null, EmbeddingDomain.Text)]
    public void classify_routes_by_file(string path, string? language, EmbeddingDomain expected)
        => ContentClassifier.Classify(path, language).Should().Be(expected);

    [Fact]
    public void classify_code_file_stays_code_even_with_comments()
        // Source code (including any comments inside it) is always the Code domain.
        => ContentClassifier.Classify("src/WithComments.cs", "csharp").Should().Be(EmbeddingDomain.Code);

    // ---- Chunk domain -----------------------------------------------------

    [Fact]
    public void chunkCreate_respects_explicit_text_domain()
    {
        var chunk = Chunk.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "# Title",
            language: "csharp", domain: EmbeddingDomain.Text);
        chunk.EmbeddingDomain.Should().Be(EmbeddingDomain.Text);
    }

    [Fact]
    public void chunkCreate_defaults_to_code_for_code_language()
    {
        var chunk = Chunk.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "public class A {}", language: "csharp");
        chunk.EmbeddingDomain.Should().Be(EmbeddingDomain.Code);
    }

    [Fact]
    public void chunkCreate_detects_text_for_markdown_language()
    {
        var chunk = Chunk.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "# Title", language: "markdown");
        chunk.EmbeddingDomain.Should().Be(EmbeddingDomain.Text);
    }

    // ---- EmbeddingOptions (per-domain profiles) ---------------------------

    [Fact]
    public void for_returns_matching_profile()
    {
        var opts = new EmbeddingOptions
        {
            Code = new EmbeddingProfileOptions { Endpoint = "http://code", Model = "cm", Dimensions = 512 },
            Text = new EmbeddingProfileOptions { Endpoint = "http://text", Model = "tm", Dimensions = 384 },
        };
        opts.For(EmbeddingDomain.Code).Endpoint.Should().Be("http://code");
        opts.For(EmbeddingDomain.Text).Endpoint.Should().Be("http://text");
    }

    [Fact]
    public void applyDomainDefaults_fills_only_empty_domains()
    {
        var opts = new EmbeddingOptions
        {
            Code = new EmbeddingProfileOptions { Endpoint = "http://code", Model = "cm", Dimensions = 512 },
            Text = new EmbeddingProfileOptions(), // empty
        };
        opts.ApplyDomainDefaults();

        // Configured domain stays untouched.
        opts.Code.Endpoint.Should().Be("http://code");
        opts.Code.Dimensions.Should().Be(512);
        // Empty domain gets the text defaults.
        opts.Text.Endpoint.Should().Be(EmbeddingOptions.DefaultTextEndpoint);
        opts.Text.Model.Should().Be(EmbeddingOptions.DefaultTextModel);
        opts.Text.Dimensions.Should().Be(EmbeddingOptions.DefaultTextDimensions);
    }

    // ---- Provider / dim-source factories ----------------------------------

    [Fact]
    public void provider_exposes_domain_and_configured_dimensions_when_not_detected()
    {
        var options = new EmbeddingOptions
        {
            Code = new EmbeddingProfileOptions { Endpoint = "http://code", Model = "cm", Dimensions = 512 },
            Text = new EmbeddingProfileOptions { Endpoint = "http://text", Model = "tm", Dimensions = 384 },
        };
        var monitor = Substitute.For<IOptionsMonitor<EmbeddingOptions>>();
        monitor.CurrentValue.Returns(options);
        var http = new HttpClient();
        var logger = Substitute.For<ILogger<OpenAICompatibleEmbeddingProvider>>();

        var codeProvider = new OpenAICompatibleEmbeddingProvider(http, EmbeddingDomain.Code, monitor, logger);
        var textProvider = new OpenAICompatibleEmbeddingProvider(http, EmbeddingDomain.Text, monitor, logger);

        codeProvider.Domain.Should().Be(EmbeddingDomain.Code);
        textProvider.Domain.Should().Be(EmbeddingDomain.Text);
        codeProvider.Dimensions.Should().Be(512);   // not detected yet -> configured value
        textProvider.Dimensions.Should().Be(384);
        codeProvider.Model.Should().Be("cm");
        textProvider.Model.Should().Be("tm");
    }

    [Fact]
    public void factory_routes_providers_by_domain()
    {
        var code = Substitute.For<IEmbeddingProvider>();
        code.Model.Returns("code-model");
        var text = Substitute.For<IEmbeddingProvider>();
        text.Model.Returns("text-model");

        var factory = new DefaultEmbeddingProviderFactory(code, text);

        factory.Code.Should().BeSameAs(code);
        factory.Text.Should().BeSameAs(text);
        factory.Get(EmbeddingDomain.Code).Should().BeSameAs(code);
        factory.Get(EmbeddingDomain.Text).Should().BeSameAs(text);
    }

    [Fact]
    public void dimSourceProvider_bridges_to_factory()
    {
        var code = Substitute.For<IEmbeddingProvider>();
        var text = Substitute.For<IEmbeddingProvider>();
        var factory = new DefaultEmbeddingProviderFactory(code, text);
        var provider = new DefaultVectorDimSourceProvider(factory);

        provider.Get(EmbeddingDomain.Code).Should().BeSameAs(code);
        provider.Get(EmbeddingDomain.Text).Should().BeSameAs(text);
    }

    // ---- Settings: per-domain flatten + config fallback -------------------

    [Fact]
    public void flatten_writes_per_domain_keys()
    {
        var dto = new AppSettingsDto
        {
            Embeddings = new EmbeddingsSettingsDto
            {
                Code = new EmbeddingProfileDto { Endpoint = "http://code", Model = "cm", Dimensions = 1024, BatchSize = 32 },
                Text = new EmbeddingProfileDto { Endpoint = "http://text", Model = "tm", Dimensions = 384, BatchSize = 64 },
            }
        };

        var kv = AppSettingsDto.Flatten(dto);

        kv["Embeddings:Code:Endpoint"].Should().Be("http://code");
        kv["Embeddings:Code:Model"].Should().Be("cm");
        kv["Embeddings:Code:Dimensions"].Should().Be("1024");
        kv["Embeddings:Text:Endpoint"].Should().Be("http://text");
        kv["Embeddings:Text:Model"].Should().Be("tm");
        kv["Embeddings:Text:Dimensions"].Should().Be("384");
        kv["Embeddings:Text:BatchSize"].Should().Be("64");
    }

    [Fact]
    public void fromConfiguration_reads_per_domain_values()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embeddings:Code:Endpoint"] = "http://code/v1/embeddings",
                ["Embeddings:Code:Model"] = "code-model",
                ["Embeddings:Code:Dimensions"] = "512",
                ["Embeddings:Code:BatchSize"] = "64",
                ["Embeddings:Text:Endpoint"] = "http://text/v1/embeddings",
                ["Embeddings:Text:Model"] = "text-model",
                ["Embeddings:Text:Dimensions"] = "384",
            })
            .Build();

        var dto = AppSettingsDto.FromConfiguration(config, new Dictionary<string, string>());

        dto.Embeddings.Code.Should().NotBeNull();
        dto.Embeddings.Code!.Endpoint.Should().Be("http://code/v1/embeddings");
        dto.Embeddings.Code.Model.Should().Be("code-model");
        dto.Embeddings.Code.Dimensions.Should().Be(512);
        dto.Embeddings.Code.BatchSize.Should().Be(64);
        dto.Embeddings.Text.Should().NotBeNull();
        dto.Embeddings.Text!.Endpoint.Should().Be("http://text/v1/embeddings");
        dto.Embeddings.Text.Dimensions.Should().Be(384);
    }

    [Fact]
    public void fromConfiguration_falls_back_to_legacy_single_endpoint()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Only the legacy single endpoint is set; each domain should inherit it.
                ["Embeddings:Endpoint"] = "http://legacy/v1/embeddings",
                ["Embeddings:Model"] = "legacy-model",
            })
            .Build();

        var dto = AppSettingsDto.FromConfiguration(config, new Dictionary<string, string>());

        dto.Embeddings.Code!.Endpoint.Should().Be("http://legacy/v1/embeddings");
        dto.Embeddings.Code.Model.Should().Be("legacy-model");
        dto.Embeddings.Text!.Endpoint.Should().Be("http://legacy/v1/embeddings");
        dto.Embeddings.Text.Model.Should().Be("legacy-model");
    }
}
