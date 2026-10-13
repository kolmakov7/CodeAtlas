using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterPhpChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> Analyze(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.php", code, SupportedLanguages.Php), default);
    private static Chunk ChunkFor(AnalyzeResult r, string name) => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == name);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty();

    [Fact]
    public async Task Class_Excludes_Methods_And_Keeps_Property()
    {
        var code = "<?php\nclass Foo {\n  public $prop = 1;\n  public function a() { return 1; }\n  public function b() { return 2; }\n}";
        var r = await Analyze(code);
        RequireNative(r);
        var foo = ChunkFor(r, "Foo");
        var a = ChunkFor(r, "a");
        foo.Source.Should().Contain("class Foo");
        foo.Source.Should().Contain("$prop");
        foo.Source.Should().NotContain("function a");
        foo.Source.Should().NotContain("function b");
        a.Source.Should().Contain("function a");
    }

    [Fact]
    public async Task Standalone_Function_Is_Chunk()
    {
        var code = "<?php\nfunction standalone($a){ return $a; }\nclass Bar { function m(){ } }";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "standalone" && s.Kind == SymbolKind.Function);
        r.Symbols.Should().Contain(s => s.Name == "Bar");
        ChunkFor(r, "standalone").Source.Should().Contain("standalone");
    }

    [Fact]
    public async Task Namespace_Is_Symbol_Not_Chunk()
    {
        var code = "<?php\nnamespace MyNs;\nclass Foo {\n  function m(){}\n}";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Namespace && s.Name == "MyNs");
        // Namespace should NOT be a chunk
        r.Chunks.Should().NotContain(c => r.Symbols.Single(s => s.Id == c.SymbolId).Kind == SymbolKind.Namespace);
        ChunkFor(r, "Foo").Source.Should().Contain("class Foo");
    }

    [Fact]
    public async Task NoSemantic_Fallback()
    {
        var code = "<?php\n// just comment\n$ x = 1;\n";
        var r = await Analyze(code);
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MultiByte_Utf8()
    {
        var code = "<?php\nclass Тест {\n  public function метод() { $s = \"Привет\"; }\n}";
        var r = await Analyze(code);
        RequireNative(r);
        var cls = ChunkFor(r, "Тест");
        var m = ChunkFor(r, "метод");
        cls.Source.Should().Contain("Тест");
        m.Source.Should().Contain("Привет");
        cls.Source.Should().NotContain("function метод");
    }
}
