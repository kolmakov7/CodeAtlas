using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Синтетические тесты CSS-чанков (Tree-sitter).
/// Чанк = rule_set (имя = селекторы), media_statement / keyframes_statement
/// (имя = шапка at-rule). Корневой stylesheet — не чанк.
/// Вложенные rule_set внутри media_statement исключаются из источника media-чанка (общее правило).
/// keyframe_block (from/to) — не чанк, остаётся внутри keyframes-чанка.
/// </summary>
public sealed class TreeSitterCssChunkTests
{
    private static FileContext Ctx(string path, string content)
        => new(Guid.NewGuid(), null, path, content, SupportedLanguages.Css, 100000);

    private static Task<AnalyzeResult> AnalyzeCss(string css)
        => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("style.css", css), default);

    private static Chunk ChunkFor(AnalyzeResult r, string symbolName)
        => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == symbolName);

    // ───────────── rule_set chunks ─────────────

    [Fact]
    public async Task RuleSets_Are_Chunks_Named_By_Selectors()
    {
        var css = """
            :root { --main: #333; }
            .a { color: red; }
            div > p { margin: 0; }
            """;
        var r = await AnalyzeCss(css);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == ":root" && s.Kind == SymbolKind.Rule);
        r.Symbols.Should().Contain(s => s.Name == ".a");
        r.Symbols.Should().Contain(s => s.Name == "div > p");
        r.Symbols.Should().OnlyContain(s => s.Language == SupportedLanguages.Css);

        var a = ChunkFor(r, ".a");
        a.Source.Should().Be(".a { color: red; }");
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── media_statement excludes nested rule_set ─────────────

    [Fact]
    public async Task MediaStatement_Excludes_Nested_RuleSets()
    {
        var css = """
            @media (min-width: 600px) {
              .b { color: blue; }
              .c { color: green; }
            }
            """;
        var r = await AnalyzeCss(css);
        RequireNative(r);

        var media = ChunkFor(r, "@media (min-width: 600px)");
        var b = ChunkFor(r, ".b");
        var c = ChunkFor(r, ".c");

        // Nested rules keep full content
        b.Source.Should().Be(".b { color: blue; }");
        c.Source.Should().Be(".c { color: green; }");

        // Media skeleton keeps its head, excludes both nested rule ranges
        media.Source.Should().Contain("@media (min-width: 600px)");
        media.Source.Should().NotContain("color: blue");
        media.Source.Should().NotContain("color: green");
        media.Source.Should().NotContain(".b");
        media.Source.Should().EndWith("}");
    }

    // ───────────── keyframes_statement: keyframe blocks are NOT chunks ─────────────

    [Fact]
    public async Task Keyframes_Are_Chunk_With_Full_Content()
    {
        var css = """
            @keyframes spin {
              from { transform: rotate(0deg); }
              to { transform: rotate(360deg); }
            }
            """;
        var r = await AnalyzeCss(css);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == "@keyframes spin" && s.Kind == SymbolKind.Rule);
        // from/to keyframe blocks are not symbols/chunks
        r.Symbols.Should().OnlyContain(s => s.Name == "@keyframes spin");

        var spin = ChunkFor(r, "@keyframes spin");
        spin.Source.Should().Contain("from { transform: rotate(0deg); }");
        spin.Source.Should().Contain("to { transform: rotate(360deg); }");
        spin.Source.Should().EndWith("}");
    }

    // ───────────── mixed file: all rule kinds coexist ─────────────

    [Fact]
    public async Task MixedFile_All_RuleKinds_Become_Chunks()
    {
        var css = """
            @import url("base.css");
            .top { color: red; }
            @media (max-width: 400px) {
              .top { color: blue; }
            }
            @keyframes fade {
              from { opacity: 0; }
            }
            """;
        var r = await AnalyzeCss(css);
        RequireNative(r);

        r.Symbols.Count(s => s.Name == ".top").Should().Be(2);
        r.Symbols.Should().Contain(s => s.Name == "@media (max-width: 400px)");
        r.Symbols.Should().Contain(s => s.Name == "@keyframes fade");
        r.Chunks.Count.Should().Be(4);

        // The media copy of .top is excluded from the media chunk
        var media = ChunkFor(r, "@media (max-width: 400px)");
        media.Source.Should().NotContain("color: blue");
    }

    // ───────────── no rules at all — sliding-window fallback ─────────────

    [Fact]
    public async Task NoRules_FallsBackToSlidingWindow()
    {
        var css = """
            @import url("base.css");
            @charset "utf-8";
            """;
        var r = await AnalyzeCss(css);
        RequireNativeOnly(r);

        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().NotBeEmpty();
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── multi-byte UTF-8 ─────────────

    [Fact]
    public async Task MultiByteUtf8_RuleContentIntact()
    {
        var css = """
            @media (min-width: 600px) {
              /* 日本語コメント — уровень медиа */
              .sum { /* расчёт отступов */ margin: 10px; }
            }
            """;
        var r = await AnalyzeCss(css);
        RequireNative(r);

        var media = ChunkFor(r, "@media (min-width: 600px)");
        var sum = ChunkFor(r, ".sum");

        // Comment inside the rule stays in the rule chunk
        sum.Source.Should().Contain("расчёт отступов");
        sum.Source.Should().Contain("margin: 10px;");

        // Comment at the media level (outside the excluded rule range) stays in the media chunk
        media.Source.Should().Contain("日本語コメント");
        media.Source.Should().NotContain("margin: 10px");
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    private static void RequireNative(AnalyzeResult r)
    {
        RequireNativeOnly(r);
        r.Symbols.Should().NotBeEmpty("native tree-sitter should be available for these tests");
    }

    private static void RequireNativeOnly(AnalyzeResult r)
        => r.Errors.Should().NotContain(e => e.Message.Contains("not available"));
}
