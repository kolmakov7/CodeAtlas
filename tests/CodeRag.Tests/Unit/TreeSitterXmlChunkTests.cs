using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Синтетические тесты XML/XSLT-чанков (Tree-sitter, libtree-sitter-xml).
/// Чанк = "container" element — элемент, имеющий в content хотя бы один дочерний элемент
/// (element или EmptyElemTag). Текстовые элементы, self-closing теги и корневой document — не чанки.
/// Имя = имя тега (с префиксом пространства имён, например "xsl:template").
/// Вложенные container-элементы исключаются из источника родительского чанка (общее правило).
/// </summary>
public sealed class TreeSitterXmlChunkTests
{
    private static FileContext Ctx(string path, string content)
        => new(Guid.NewGuid(), null, path, content, SupportedLanguages.Xml, 100000);

    private static Task<AnalyzeResult> AnalyzeXml(string xml)
        => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("data.xml", xml), default);

    private static Chunk ChunkFor(AnalyzeResult r, string symbolName)
        => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == symbolName);

    // ───────────── container elements are chunks, text-only / self-closing are not ─────────────

    [Fact]
    public async Task ContainerElements_Are_Chunks_TextOnlyAndSelfClosingAreNot()
    {
        var xml = """
            <?xml version="1.0"?>
            <catalog>
              <book id="bk1">
                <author>Ann</author>
                <title>Book 1</title>
              </book>
              <note>text only</note>
              <empty/>
            </catalog>
            """;
        var r = await AnalyzeXml(xml);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == "catalog" && s.Kind == SymbolKind.Element);
        r.Symbols.Should().Contain(s => s.Name == "book");
        // Text-only and self-closing elements are not symbols
        var notSymbols = new HashSet<string> { "author", "title", "note", "empty" };
        r.Symbols.Should().NotContain(s => notSymbols.Contains(s.Name));
        r.Symbols.Should().OnlyContain(s => s.Language == SupportedLanguages.Xml);
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));

        var catalog = ChunkFor(r, "catalog");
        // Parent keeps non-chunk content, excludes the child book range
        catalog.Source.Should().Contain("<note>text only</note>");
        catalog.Source.Should().Contain("<empty/>");
        catalog.Source.Should().NotContain("bk1");
    }

    // ───────────── nesting: parent excludes child container ranges ─────────────

    [Fact]
    public async Task NestedContainers_Parent_Excludes_Child_Range()
    {
        var xml = """
            <catalog>
              <book id="bk1">
                <meta>
                  <author>A</author>
                </meta>
                <pages>120</pages>
              </book>
            </catalog>
            """;
        var r = await AnalyzeXml(xml);
        RequireNative(r);

        var catalog = ChunkFor(r, "catalog");
        var book = ChunkFor(r, "book");
        var meta = ChunkFor(r, "meta");

        meta.Source.Should().Contain("<author>A</author>");
        book.Source.Should().Contain("<pages>120</pages>");
        book.Source.Should().NotContain("author");
        catalog.Source.Should().NotContain("bk1");
        catalog.Source.Should().EndWith("</catalog>");
    }

    // ───────────── XSLT: templates and stylesheet containers ─────────────

    [Fact]
    public async Task Xslt_Templates_Are_Chunks_ExcludedFrom_Stylesheet()
    {
        var xml = """
            <?xml version="1.0"?>
            <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/catalog">
                <html><body>
                  <xsl:for-each select="book">
                    <div class="book"><xsl:value-of select="title"/></div>
                  </xsl:for-each>
                </body></html>
              </xsl:template>
              <xsl:template match="book[@id='bk102']">
                <p>special</p>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await AnalyzeXml(xml);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == "xsl:stylesheet" && s.Kind == SymbolKind.Element);
        r.Symbols.Count(s => s.Name == "xsl:template").Should().Be(2);
        r.Symbols.Should().Contain(s => s.Name == "xsl:for-each");
        r.Symbols.Should().Contain(s => s.Name == "div");
        var notSymbols = new HashSet<string> { "p" };
        r.Symbols.Should().NotContain(s => notSymbols.Contains(s.Name));

        var sheet = ChunkFor(r, "xsl:stylesheet");
        sheet.Source.Should().Contain("xmlns:xsl");
        sheet.Source.Should().NotContain("select=\"title\"");
        sheet.Source.Should().NotContain("special");

        // Multi-level nesting: template > html > body > for-each > div —
        // the template source keeps only its own skeleton (all nested containers excluded)
        var templates = r.Chunks
            .Where(c => c.SymbolId != null)
            .Where(c => r.Symbols.First(s => s.Id == c.SymbolId).Name == "xsl:template")
            .ToList();
        templates.Should().HaveCount(2);
        var firstTemplate = templates[0];
        firstTemplate.Source.Should().StartWith("<xsl:template match=\"/catalog\">");
        firstTemplate.Source.Should().NotContain("select=\"title\"");
        firstTemplate.Source.Should().NotContain("special");

        // The leaf-level content lives in the innermost chunk (div)
        var div = ChunkFor(r, "div");
        div.Source.Should().Contain("<xsl:value-of select=\"title\"/>");

        // The second template has no container descendants — its text stays in the template chunk
        var secondTemplate = templates[1];
        secondTemplate.Source.Should().Contain("<p>special</p>");
    }

    // ───────────── self-closing children still make a container ─────────────

    [Fact]
    public async Task SelfClosingChildren_Make_Parent_A_Chunk()
    {
        var xml = """
            <svg>
              <rect x="0" y="0"/>
              <circle r="5"/>
            </svg>
            """;
        var r = await AnalyzeXml(xml);
        RequireNative(r);

        r.Symbols.Should().ContainSingle(s => s.Name == "svg");
        var svg = ChunkFor(r, "svg");
        svg.Source.Should().Contain("<rect x=\"0\" y=\"0\"/>");
        svg.Source.Should().Contain("<circle r=\"5\"/>");
    }

    // ───────────── no container elements — sliding-window fallback ─────────────

    [Fact]
    public async Task TextOnlyRoot_FallsBackToSlidingWindow()
    {
        var xml = """
            <note>
              plain text only, no child elements
            </note>
            """;
        var r = await AnalyzeXml(xml);
        RequireNativeOnly(r);

        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().NotBeEmpty();
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── multi-byte UTF-8 ─────────────

    [Fact]
    public async Task MultiByteUtf8_ElementContentIntact()
    {
        var xml = """
            <catalog>
              <book id="bk1">
                <author>Иванов</author>
                <title>日本語</title>
              </book>
            </catalog>
            """;
        var r = await AnalyzeXml(xml);
        RequireNative(r);

        var book = ChunkFor(r, "book");
        book.Source.Should().Contain("Иванов");
        book.Source.Should().Contain("日本語");

        var catalog = ChunkFor(r, "catalog");
        catalog.Source.Should().NotContain("Иванов");
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    private static void RequireNative(AnalyzeResult r)
    {
        RequireNativeOnly(r);
        r.Symbols.Should().NotBeEmpty("native tree-sitter xml should be available for these tests");
    }

    private static void RequireNativeOnly(AnalyzeResult r)
        => r.Errors.Should().NotContain(e => e.Message.Contains("not available"));
}
