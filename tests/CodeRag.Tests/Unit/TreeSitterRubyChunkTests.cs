using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterRubyChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> Analyze(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.rb", code, SupportedLanguages.Ruby), default);
    private static Chunk ChunkFor(AnalyzeResult r, string name) => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == name);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty();

    [Fact]
    public async Task Class_Excludes_Methods()
    {
        var code = "class MyClass\n  def my_method\n    puts \"hi\"\n  end\n  def another\n  end\nend";
        var r = await Analyze(code);
        RequireNative(r);
        var cls = ChunkFor(r, "MyClass");
        var m1 = ChunkFor(r, "my_method");
        cls.Source.Should().Contain("class MyClass");
        cls.Source.Should().NotContain("def my_method");
        m1.Source.Should().Contain("my_method");
    }

    [Fact]
    public async Task Module_And_Class_Nested()
    {
        var code = "module Outer\n  class Inner\n    def inner_method\n    end\n  end\n  def outer_method\n  end\nend";
        var r = await Analyze(code);
        RequireNative(r);
        var outer = ChunkFor(r, "Outer");
        var inner = ChunkFor(r, "Inner");
        var outerM = ChunkFor(r, "outer_method");
        outer.Source.Should().Contain("module Outer");
        outer.Source.Should().NotContain("class Inner");
        outer.Source.Should().NotContain("outer_method");
        inner.Source.Should().Contain("class Inner");
        inner.Source.Should().NotContain("inner_method");
        outerM.Source.Should().Contain("outer_method");
    }

    [Fact]
    public async Task Singleton_Method_Is_Chunk()
    {
        var code = "class Foo\n  def self.build\n    new\n  end\nend";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "build");
    }

    [Fact]
    public async Task NoSemantic_Fallback()
    {
        var code = "# just comment\nputs \"hello\"\n";
        var r = await Analyze(code);
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MultiByte_Utf8()
    {
        var code = "class MyClass\n  def my_method\n    s = \"Привет мир\"\n  end\nend";
        var r = await Analyze(code);
        RequireNative(r);
        var cls = ChunkFor(r, "MyClass");
        var m = ChunkFor(r, "my_method");
        cls.Source.Should().Contain("MyClass");
        m.Source.Should().Contain("Привет");
        cls.Source.Should().NotContain("def my_method");
    }
}
