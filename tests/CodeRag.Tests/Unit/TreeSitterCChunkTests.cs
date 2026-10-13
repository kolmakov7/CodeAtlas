using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterCChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> AnalyzeC(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.c", code, SupportedLanguages.C), default);
    private static Task<AnalyzeResult> AnalyzeCpp(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.cpp", code, SupportedLanguages.Cpp), default);
    private static Chunk ChunkFor(AnalyzeResult r, string name) => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == name);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty();

    [Fact]
    public async Task Struct_And_Function_Are_Chunks_C()
    {
        var code = "struct Foo { int x; };\nvoid myFunc() { int y=1; }";
        var r = await AnalyzeC(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "Foo" && s.Kind == SymbolKind.Class);
        r.Symbols.Should().Contain(s => s.Name == "myFunc");
        ChunkFor(r, "Foo").Source.Should().Contain("struct Foo");
        ChunkFor(r, "myFunc").Source.Should().Contain("myFunc()");
    }

    [Fact]
    public async Task Cpp_Class_Specifier_Still_Works()
    {
        var code = "class MyClass { public: void methodOne(); int value; };\nvoid MyClass::methodOne() { value = 1; }\n";
        var r = await AnalyzeCpp(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "MyClass");
        ChunkFor(r, "MyClass").Source.Should().Contain("MyClass");
    }

    [Fact]
    public async Task Struct_Containing_Struct_Excluded()
    {
        var code = "struct Outer { int x; struct Inner { int y; }; };";
        var r = await AnalyzeC(code);
        RequireNative(r);
        var outer = ChunkFor(r, "Outer");
        var inner = ChunkFor(r, "Inner");
        inner.Source.Should().Contain("Inner");
        outer.Source.Should().Contain("Outer");
        outer.Source.Should().NotContain("Inner { int y; }");
        outer.Source.Should().Contain("int x;");
    }

    [Fact]
    public async Task NoSemantic_Fallback_C()
    {
        var code = "#include <stdio.h>\n#define MAX 10\n";
        var r = await AnalyzeC(code);
        r.Chunks.Should().NotBeEmpty(); // fallback
    }

    [Fact]
    public async Task MultiByte_Utf8_C()
    {
        var code = "// Привет\nstruct Тест { int x; };\nvoid метод() { int s=1; }\n";
        var r = await AnalyzeC(code);
        RequireNative(r);
        var str = ChunkFor(r, "Тест");
        str.Source.Should().Contain("Тест");
        // method name extraction for C may be "метод"
    }
}
