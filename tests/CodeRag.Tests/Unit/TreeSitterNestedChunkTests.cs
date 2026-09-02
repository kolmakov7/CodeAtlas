using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Синтетические тесты вложенных семантических чанков (Tree-sitter).
/// Правило: если дочерняя AST-нода стала самостоятельным чанком, её диапазон исключается
/// из источника родительского чанка; потомок сохраняет полное содержимое.
/// Обычные вложенные ноды (if/for/block), не являющиеся чанками, не вырезаются.
/// </summary>
public sealed class TreeSitterNestedChunkTests
{
    private static FileContext Ctx(string path, string content, string lang)
        => new(Guid.NewGuid(), null, path, content, lang, 100000);

    private static async Task<AnalyzeResult> AnalyzeJs(string code)
        => await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("nested.js", code, SupportedLanguages.JavaScript), default);

    private static Chunk ChunkFor(AnalyzeResult r, string symbolName)
        => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == symbolName);

    // ───────────── one child ─────────────

    [Fact]
    public async Task OneChild_Parent_Excludes_Child_Range()
    {
        var js = """
            var foo = function() {
                var bar = function() {
                    return 1;
                };
                somethingToAnother();
            };
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        var foo = ChunkFor(r, "foo");
        var bar = ChunkFor(r, "bar");

        // Child (function_expression node) keeps full content
        bar.Source.Should().StartWith("function() {");
        bar.Source.Should().Contain("return 1;");

        // Parent keeps its own text, but the child node range is excluded.
        // The declarator prefix `var bar = ` is not part of the child node, so it stays.
        foo.Source.Should().StartWith("function() {");
        foo.Source.Should().Contain("var bar = ");
        foo.Source.Should().Contain("somethingToAnother();");
        foo.Source.Should().NotContain("return 1;");
        foo.Source.Should().EndWith("}");
    }

    // ───────────── several children ─────────────

    [Fact]
    public async Task SeveralChildren_Class_Excludes_All_Methods()
    {
        var js = """
            class Repo {
                title = "Repo";
                save(item) { return item; }
                load(item) { return item; }
            }
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        var repo = ChunkFor(r, "Repo");
        var save = ChunkFor(r, "save");
        var load = ChunkFor(r, "load");

        // Siblings keep full content
        save.Source.Should().Be("save(item) { return item; }");
        load.Source.Should().Be("load(item) { return item; }");

        // Class skeleton keeps non-chunk members, excludes all method ranges
        repo.Source.Should().Contain("class Repo {");
        repo.Source.Should().Contain("title = \"Repo\";");
        repo.Source.Should().NotContain("save(item)");
        repo.Source.Should().NotContain("load(item)");
    }

    // ───────────── three levels ─────────────

    [Fact]
    public async Task ThreeLevels_Each_Level_Excludes_Its_Children_Only()
    {
        var js = """
            function outer() {
                pre();
                function middle() {
                    mid();
                    function inner() {
                        deep();
                    }
                    post();
                }
                tail();
            }
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        var outer = ChunkFor(r, "outer");
        var middle = ChunkFor(r, "middle");
        var inner = ChunkFor(r, "inner");

        // Deepest keeps full content
        inner.Source.Should().Contain("deep();");

        // Middle excludes inner, keeps own statements
        middle.Source.Should().Contain("function middle() {");
        middle.Source.Should().Contain("mid();");
        middle.Source.Should().Contain("post();");
        middle.Source.Should().NotContain("function inner");
        middle.Source.Should().NotContain("deep();");

        // Outer excludes the whole middle range (including inner), keeps own statements
        outer.Source.Should().Contain("function outer() {");
        outer.Source.Should().Contain("pre();");
        outer.Source.Should().Contain("tail();");
        outer.Source.Should().NotContain("function middle");
        outer.Source.Should().NotContain("mid();");
        outer.Source.Should().NotContain("deep();");
    }

    // ───────────── siblings ─────────────

    [Fact]
    public async Task Siblings_EachExcluded_SiblingContentIntact()
    {
        var js = """
            function make(a) {
                function left() { return a; }
                var shared = a * 2;
                function right() { return shared; }
                return [left, right];
            }
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        var make = ChunkFor(r, "make");
        var left = ChunkFor(r, "left");
        var right = ChunkFor(r, "right");

        left.Source.Should().Contain("function left() { return a; }");
        right.Source.Should().Contain("function right() { return shared; }");

        make.Source.Should().Contain("function make(a) {");
        make.Source.Should().Contain("var shared = a * 2;");
        make.Source.Should().Contain("return [left, right];");
        make.Source.Should().NotContain("function left");
        make.Source.Should().NotContain("function right");
    }

    // ───────────── ordinary nested node that is NOT a chunk ─────────────

    [Fact]
    public async Task NonChunkNestedNodes_AreNotCut()
    {
        var js = """
            function plain() {
                if (x) {
                    for (var i = 0; i < 10; i++) {
                        console.log(i);
                    }
                }
            }
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        var plain = ChunkFor(r, "plain");
        plain.Source.Should().Contain("if (x) {");
        plain.Source.Should().Contain("for (var i = 0; i < 10; i++) {");
        plain.Source.Should().Contain("console.log(i);");
        plain.Source.Should().EndWith("}");
    }

    // ───────────── function_expression (var foo = function() {}) ─────────────

    [Fact]
    public async Task FunctionExpression_Named_From_Declarator()
    {
        var js = """
            var anon = function(a, b) {
                return a + b;
            };
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        r.Symbols.Should().Contain(s => s.Name == "anon" && s.Kind == SymbolKind.Function);
        var chunk = ChunkFor(r, "anon");
        chunk.Source.Should().Contain("return a + b;");
    }

    // ───────────── multi-byte UTF-8 ─────────────

    [Fact]
    public async Task MultiByteUtf8_ExclusionKeepsSourceIntact()
    {
        var js = """
            function calc() {
                // расчёт суммы
                var s = "сумма";
                function helper() {
                    return s;
                }
                return helper();
            }
            """;
        var r = await AnalyzeJs(js);
        RequireNative(r);

        var calc = ChunkFor(r, "calc");
        var helper = ChunkFor(r, "helper");

        // Non-ASCII text of the parent survives exclusion intact
        calc.Source.Should().Contain("расчёт суммы");
        calc.Source.Should().Contain("сумма");
        calc.Source.Should().NotContain("function helper");
        calc.Source.Should().NotContain("return s;");

        helper.Source.Should().Contain("function helper() {");
        helper.Source.Should().Contain("return s;");

        // Every chunk source must be non-empty and intact
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source));
    }

    // ───────────── cross-language sanity: Python class/method ─────────────

    [Fact]
    public async Task Python_Class_Excludes_Methods()
    {
        var py = """
            class OrderService:
                def save(self, item):
                    print(item)

                def process(self, item):
                    self.save(item)
            """;
        var r = await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("app.py", py, SupportedLanguages.Python), default);
        RequireNative(r);

        var cls = r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == "OrderService");
        var save = r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == "save");

        save.Source.Should().Contain("def save(self, item):");
        save.Source.Should().Contain("print(item)");
        cls.Source.Should().Contain("class OrderService:");
        cls.Source.Should().NotContain("def save");
        cls.Source.Should().NotContain("def process");
    }

    private static void RequireNative(AnalyzeResult r)
    {
        // If native Tree-sitter is unavailable the analyzer falls back to sliding window;
        // nested-exclusion assertions are meaningless then.
        r.Symbols.Should().NotBeEmpty("native tree-sitter should be available for these tests");
    }
}
