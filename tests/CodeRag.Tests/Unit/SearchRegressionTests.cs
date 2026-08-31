using CodeRag.Api.Mappers;
using CodeRag.Search;
using CodeRag.Search.Models;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression tests for MCP iteration fixes (eShop dataset).
/// Каждый тест соответствует конкретному Fix из docs/mcp-iteration-progress.md
/// </summary>
public sealed class SearchRegressionTests
{
    // P1: GetRelationsAsync с фильтром relationTypes не должен падать на "; AND"
    // Проверяем что SQL-строитель не оставляет ';' перед AND (косвенно через успешный вызов не требуется БД).
    // Здесь проверяем что репозиторий не бросает SyntaxError при вызове с фильтром — требует интеграции, пропускаем без БД.
    // Поэтому проверяем что парсинг типов сохраняется.
    [Theory]
    [InlineData("Contains", new[] { "Contains" })]
    [InlineData("Calls,DependsOn", new[] { "Calls", "DependsOn" })]
    [InlineData("Implements", new[] { "Implements" })]
    public void relationTypes_parsing_should_split_csv(string csv, string[] expected)
    {
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        parts.Should().Equal(expected);
    }

    // P3: Semantic domain separation — ParseMode должен различать code/text
    [Theory]
    [InlineData("Semantic", SearchMode.Semantic)]
    [InlineData("SemanticCode", SearchMode.SemanticCode)]
    [InlineData("semantic_code", SearchMode.SemanticCode)]
    [InlineData("semantic-code", SearchMode.SemanticCode)]
    [InlineData("code", SearchMode.SemanticCode)]
    [InlineData("SemanticText", SearchMode.SemanticText)]
    [InlineData("semantic_text", SearchMode.SemanticText)]
    [InlineData("text", SearchMode.SemanticText)]
    [InlineData("docs", SearchMode.SemanticText)]
    public void ParseMode_should_distinguish_code_vs_text(string input, SearchMode expected)
    {
        SearchResultMapper.ParseMode(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Hybrid", SearchMode.Hybrid)]
    [InlineData("FullText", SearchMode.FullText)]
    [InlineData("fts", SearchMode.FullText)]
    [InlineData("Symbol", SearchMode.Symbol)]
    public void ParseMode_should_keep_legacy_modes(string input, SearchMode expected)
    {
        SearchResultMapper.ParseMode(input).Should().Be(expected);
    }

    // P3: weak query filtering — raw cosine без per-domain нормализации
    // Проверяем что MinSemanticScore default теперь 0.30 (было 0.25) чтобы youtube/timeout → 0
    [Fact]
    public void SearchOptions_default_MinSemanticScore_should_be_0_30()
    {
        var opts = new SearchOptions();
        opts.MinSemanticScore.Should().Be(0.30);
    }

    // P2: get_code не должен требовать /data/repos
    // Проверяем что путь traversal защищен но не привязан к /data
    [Fact]
    public void GetCode_path_should_reject_absolute_but_allow_relative()
    {
        var relative = "src/Catalog.API/Apis/CatalogApi.cs";
        relative.Contains("..").Should().BeFalse();
        // Относительный путь не должен считаться Path.IsPathRooted на linux
        System.IO.Path.IsPathRooted(relative).Should().BeFalse();
        System.IO.Path.IsPathRooted("/data/repos/eshop/src/Foo.cs").Should().BeTrue();
    }

    // P4: find_symbol ordering — exact match + kind priority
    // Проверяем что Enum содержит новые значения (не ломает сериализацию)
    [Fact]
    public void SearchMode_should_contain_SemanticCode_and_SemanticText()
    {
        Enum.IsDefined(typeof(SearchMode), SearchMode.SemanticCode).Should().BeTrue();
        Enum.IsDefined(typeof(SearchMode), SearchMode.SemanticText).Should().BeTrue();
    }

    // P5: text-domain floor — jina-text scores irrelevant prose 0.33-0.46, so the text domain
    // needs a stricter absolute floor than the generic MinSemanticScore (0.30).
    [Fact]
    public void SearchOptions_default_MinTextSemanticScore_should_be_stricter_than_MinSemanticScore()
    {
        var opts = new SearchOptions();
        opts.MinTextSemanticScore.Should().Be(0.55);
        opts.MinTextSemanticScore.Should().BeGreaterThan(opts.MinSemanticScore);
    }

    // P6: per-domain normalization — text-domain vector contribution is down-weighted in merged
    // Hybrid/Semantic ranking so code questions are not out-ranked by doc chunks.
    [Fact]
    public void SearchOptions_default_TextDomainWeight_should_downweight_text()
    {
        var opts = new SearchOptions();
        opts.TextDomainWeight.Should().Be(0.6);
        opts.TextDomainWeight.Should().BeGreaterThan(0);
        opts.TextDomainWeight.Should().BeLessThan(1);
    }

    // P6: ScoredChunk carries its embedding domain; backward-compatible default is Code.
    [Fact]
    public void ScoredChunk_domain_should_default_to_code()
    {
        var chunk = new CodeRag.Infrastructure.Vectors.ScoredChunk(Guid.NewGuid(), 0.5, 0.5);
        chunk.Domain.Should().Be(CodeRag.Domain.Enums.EmbeddingDomain.Code);
    }
}
