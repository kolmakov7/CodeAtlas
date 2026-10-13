using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterJavaChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> Analyze(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("A.java", code, SupportedLanguages.Java), default);
    private static Chunk ChunkFor(AnalyzeResult r, string name) => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == name);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty("native tree-sitter should be available");

    [Fact]
    public async Task Class_Is_Chunk_While_Package_And_Import_Are_Not()
    {
        var code = "package com.example;\nimport java.util.*;\npublic class Foo {}\n";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Class && s.Name == "Foo");
        r.Symbols.Should().NotContain(s => s.Name == "com.example");
        r.Chunks.Should().Contain(c => r.Symbols.Single(s => s.Id == c.SymbolId).Name == "Foo");
    }

    [Fact]
    public async Task Class_Excludes_Methods_And_InnerClass()
    {
        var code = """
            public class Outer {
                private int x;
                public void foo() { int y=1; }
                public class Inner {
                    public void innerFoo() {}
                }
                public void bar() {}
            }
            """;
        var r = await Analyze(code);
        RequireNative(r);
        var outer = ChunkFor(r, "Outer");
        var foo = ChunkFor(r, "foo");
        var inner = ChunkFor(r, "Inner");
        var innerFoo = ChunkFor(r, "innerFoo");
        var bar = ChunkFor(r, "bar");
        foo.Source.Should().Contain("foo()");
        innerFoo.Source.Should().Contain("innerFoo()");
        outer.Source.Should().Contain("private int x;");
        outer.Source.Should().NotContain("foo()");
        outer.Source.Should().NotContain("innerFoo");
        outer.Source.Should().NotContain("class Inner");
        inner.Source.Should().Contain("class Inner");
        inner.Source.Should().NotContain("innerFoo");
        bar.Source.Should().Contain("bar()");
    }

    [Fact]
    public async Task Interface_And_Record_Are_Chunks()
    {
        var code = "public interface MyIf { void m(); }\npublic record Point(int x, int y) {}\n";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "MyIf");
        r.Symbols.Should().Contain(s => s.Name == "Point");
    }

    [Fact]
    public async Task NoSemanticElements_FallsBack()
    {
        var code = "package com.example;\nimport java.util.*;\n// just comments\n";
        var r = await Analyze(code);
        // Zero chunks -> sliding window fallback produces at least one chunk with null SymbolId
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MultiByte_Utf8_Preserved()
    {
        var code = "// Привет мир\npublic class Тест {\n  public void метод() { String s = \"Привет\"; }\n}\n";
        var r = await Analyze(code);
        RequireNative(r);
        var outer = ChunkFor(r, "Тест");
        var method = ChunkFor(r, "метод");
        outer.Source.Should().Contain("Тест");
        method.Source.Should().Contain("Привет");
        outer.Source.Should().NotContain("метод()");
    }

    [Fact]
    public async Task Siblings_Each_Excluded()
    {
        var code = "public class A { void m1() {} void m2() {} void m3() {} }";
        var r = await Analyze(code);
        RequireNative(r);
        var a = ChunkFor(r, "A");
        a.Source.Should().Contain("class A");
        a.Source.Should().NotContain("m1()");
        a.Source.Should().NotContain("m2()");
        a.Source.Should().NotContain("m3()");
    }
}
