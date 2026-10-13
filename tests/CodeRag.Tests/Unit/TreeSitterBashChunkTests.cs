using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterBashChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> Analyze(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.sh", code, SupportedLanguages.Bash), default);
    private static Chunk ChunkFor(AnalyzeResult r, string name) => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == name);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty();

    [Fact]
    public async Task Function_Is_Chunk()
    {
        var code = "my_func() { echo hello; }\nother() { echo other; }";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "my_func");
        ChunkFor(r, "my_func").Source.Should().Contain("my_func()");
    }

    [Fact]
    public async Task Nested_Function_Excluded()
    {
        var code = "func_outer() {\n  echo outer\n  func_inner() {\n    echo inner\n  }\n  echo done\n}\nfunc2() { echo 2; }";
        var r = await Analyze(code);
        RequireNative(r);
        var outer = ChunkFor(r, "func_outer");
        var inner = ChunkFor(r, "func_inner");
        inner.Source.Should().Contain("func_inner");
        outer.Source.Should().Contain("echo outer");
        outer.Source.Should().Contain("echo done");
        outer.Source.Should().NotContain("func_inner");
        outer.Source.Should().NotContain("echo inner");
    }

    [Fact]
    public async Task NonFunction_Content_Not_Cut()
    {
        var code = "my_func() { if true; then echo hi; fi; for i in 1 2; do echo $i; done; }";
        var r = await Analyze(code);
        RequireNative(r);
        var fn = ChunkFor(r, "my_func");
        fn.Source.Should().Contain("if true");
        fn.Source.Should().Contain("for i");
    }

    [Fact]
    public async Task NoFunction_Fallback()
    {
        var code = "echo hello\necho world\n";
        var r = await Analyze(code);
        r.Chunks.Should().NotBeEmpty(); // fallback sliding window
        r.Symbols.Should().BeEmpty();
    }

    [Fact]
    public async Task MultiByte_Utf8()
    {
        var code = "func_тест() { echo \"Привет\"; s=\"мир\"; }\n";
        var r = await Analyze(code);
        RequireNative(r);
        ChunkFor(r, "func_тест").Source.Should().Contain("Привет");
    }
}
