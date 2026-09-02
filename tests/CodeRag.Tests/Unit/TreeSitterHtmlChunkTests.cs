using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Синтетические тесты HTML-чанков (Tree-sitter).
/// Чанк = sectioning/landmark-элемент (article/aside/section/nav/main/header/footer/form/
/// fieldset/details/dialog/template/figure/table), ЛЮБОЙ элемент с явным id (семантический
/// якорь разработчика), ЛИБО script/style с inline-контентом.
/// document/html/head/body и обычные структурные элементы — не чанки.
/// Вложенные секции исключаются из источника родительского чанка (общее правило).
/// </summary>
public sealed class TreeSitterHtmlChunkTests
{
    private static FileContext Ctx(string path, string content)
        => new(Guid.NewGuid(), null, path, content, SupportedLanguages.Html, 100000);

    private static Task<AnalyzeResult> AnalyzeHtml(string html)
        => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("page.html", html), default);

    private static Chunk ChunkFor(AnalyzeResult r, string symbolName)
        => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == symbolName);

    // ───────────── sectioning elements ─────────────

    [Fact]
    public async Task SectioningElements_Are_Chunks_StructuralWrappersAreNot()
    {
        var html = """
            <!DOCTYPE html>
            <html>
              <head><title>T</title></head>
              <body>
                <header>logo</header>
                <nav id="menu"><a href="/">home</a></nav>
                <main>
                  <article>text</article>
                  <div>plain div</div>
                </main>
                <footer>copyright</footer>
              </body>
            </html>
            """;
        var r = await AnalyzeHtml(html);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == "header" && s.Kind == SymbolKind.Section);
        r.Symbols.Should().Contain(s => s.Name == "nav#menu");
        r.Symbols.Should().Contain(s => s.Name == "main");
        r.Symbols.Should().Contain(s => s.Name == "article");
        r.Symbols.Should().Contain(s => s.Name == "footer");
        // Structural wrappers and plain elements are not symbols
        var notSymbols = new HashSet<string> { "html", "head", "body", "div", "title" };
        r.Symbols.Should().NotContain(s => notSymbols.Contains(s.Name));
        r.Symbols.Should().OnlyContain(s => s.Language == SupportedLanguages.Html);
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── nested sections: parent excludes child range ─────────────

    [Fact]
    public async Task NestedSections_Parent_Excludes_Child_Range()
    {
        var html = """
            <body>
              <section id="s1">
                <p>outer intro</p>
                <section id="s2">
                  <p>inner text</p>
                </section>
                <p>outer outro</p>
              </section>
            </body>
            """;
        var r = await AnalyzeHtml(html);
        RequireNative(r);

        var s1 = ChunkFor(r, "section#s1");
        var s2 = ChunkFor(r, "section#s2");

        // Child keeps full content
        s2.Source.Should().StartWith("<section id=\"s2\">");
        s2.Source.Should().Contain("inner text");
        s2.Source.Should().EndWith("</section>");

        // Parent keeps its own text, excludes the child section range
        s1.Source.Should().Contain("<section id=\"s1\">");
        s1.Source.Should().Contain("outer intro");
        s1.Source.Should().Contain("outer outro");
        s1.Source.Should().NotContain("inner text");
        s1.Source.Should().NotContain("section id=\"s2\"");
    }

    // ───────────── id = semantic anchor on any element ─────────────

    [Fact]
    public async Task AnyElementWithId_Is_Chunk_WithoutIdIsNot()
    {
        var html = """
            <body>
              <div id="hero"><h1>Title</h1></div>
              <div><p>no id</p></div>
              <span id="badge">badge</span>
            </body>
            """;
        var r = await AnalyzeHtml(html);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == "div#hero");
        r.Symbols.Should().Contain(s => s.Name == "span#badge");
        var notSymbols = new HashSet<string> { "div", "span", "h1", "p" };
        r.Symbols.Should().NotContain(s => notSymbols.Contains(s.Name));

        var hero = ChunkFor(r, "div#hero");
        hero.Source.Should().Contain("<h1>Title</h1>");
        hero.Source.Should().EndWith("</div>");
    }

    // ───────────── script/style: inline content is a chunk, external is not ─────────────

    [Fact]
    public async Task ScriptStyle_InlineContent_Is_Chunk_ExternalIsNot()
    {
        var html = """
            <body>
              <div id="wrap">
                <script src="app.js"></script>
                <script>
                  var x = 1;
                </script>
                <style>
                  .a { color: red; }
                </style>
              </div>
            </body>
            """;
        var r = await AnalyzeHtml(html);
        RequireNative(r);

        // Only the script with inline content became a symbol; external <script src> did not
        r.Symbols.Count(s => s.Name == "script").Should().Be(1);
        r.Symbols.Count(s => s.Name == "style").Should().Be(1);

        var wrap = ChunkFor(r, "div#wrap");
        wrap.Source.Should().Contain("<div id=\"wrap\">");
        wrap.Source.Should().NotContain("var x = 1;");
        wrap.Source.Should().NotContain("color: red");

        var script = ChunkFor(r, "script");
        script.Source.Should().Contain("var x = 1;");
        var style = ChunkFor(r, "style");
        style.Source.Should().Contain("color: red;");
    }

    // ───────────── html/head/body never chunks, even with id ─────────────

    [Fact]
    public async Task HtmlHeadBody_Never_Chunks()
    {
        var html = """
            <html id="h">
              <head id="hd"><title>T</title></head>
              <body id="bd"><p>hi</p></body>
            </html>
            """;
        var r = await AnalyzeHtml(html);
        RequireNativeOnly(r);

        r.Symbols.Should().BeEmpty();
        // Zero semantic chunks — sliding-window fallback still produces chunks
        r.Chunks.Should().NotBeEmpty();
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── no semantic elements at all — sliding-window fallback ─────────────

    [Fact]
    public async Task NoSemanticElements_FallsBackToSlidingWindow()
    {
        var html = """
            <html><body>
              <div><p>one</p></div>
              <div><p>two</p></div>
              <div><p>three</p></div>
            </body></html>
            """;
        var r = await AnalyzeHtml(html);
        RequireNativeOnly(r);

        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().NotBeEmpty();
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── multi-byte UTF-8 ─────────────

    [Fact]
    public async Task MultiByteUtf8_SectionContentIntact()
    {
        var html = """
            <body>
              <section id="main">
                <p>Содержание: расчёт суммы — 日本語テキスト</p>
                <aside>
                  <p>附言</p>
                </aside>
                <p>заключение</p>
              </section>
            </body>
            """;
        var r = await AnalyzeHtml(html);
        RequireNative(r);

        var main = ChunkFor(r, "section#main");
        var aside = ChunkFor(r, "aside");

        main.Source.Should().Contain("расчёт суммы — 日本語テキスト");
        main.Source.Should().Contain("заключение");
        main.Source.Should().NotContain("附言");
        aside.Source.Should().Contain("附言");
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    private static void RequireNative(AnalyzeResult r)
    {
        // If native Tree-sitter is unavailable the analyzer produces no symbols at all;
        // then the semantic assertions below are meaningless.
        RequireNativeOnly(r);
        r.Symbols.Should().NotBeEmpty("native tree-sitter should be available for these tests");
    }

    private static void RequireNativeOnly(AnalyzeResult r)
        => r.Errors.Should().NotContain(e => e.Message.Contains("not available"));
}
