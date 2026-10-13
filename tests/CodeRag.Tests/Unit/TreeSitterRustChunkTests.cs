using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterRustChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> Analyze(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.rs", code, SupportedLanguages.Rust), default);
    private static Chunk ChunkFor(AnalyzeResult r, string name) => r.Chunks.Single(c => c.SymbolId != null && r.Symbols.Single(s => s.Id == c.SymbolId).Name == name);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty();

    [Fact]
    public async Task Mod_Struct_Impl_Function_Chunks()
    {
        var code = "mod mymod { pub struct S; impl S { fn foo(&self) {} } }";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "mymod");
        r.Symbols.Should().Contain(s => s.Name == "S");
        r.Symbols.Should().Contain(s => s.Name == "foo");
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Impl_Excludes_Methods_MultiLevel()
    {
        var code = """
            mod mymod {
              struct Inner { x: i32 }
              impl Inner {
                fn new() -> Self { Self { x: 0 } }
                fn get(&self) -> i32 { self.x }
              }
            }
            """;
        var r = await Analyze(code);
        RequireNative(r);
        var mod = ChunkFor(r, "mymod");
        var implChunk = r.Chunks.First(c => r.Symbols.Single(s => s.Id == c.SymbolId).Name == "Inner" && c.Source.Contains("impl"));
        var nw = ChunkFor(r, "new");
        mod.Source.Should().Contain("mod mymod");
        mod.Source.Should().NotContain("fn new");
        mod.Source.Should().NotContain("fn get");
        implChunk.Source.Should().Contain("impl Inner");
        implChunk.Source.Should().NotContain("fn new");
        nw.Source.Should().Contain("fn new()");
    }

    [Fact]
    public async Task SeveralChildren_Excluded()
    {
        var code = "fn top() {}\nstruct S;\nenum E { A, B }\n";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "top");
        r.Symbols.Should().Contain(s => s.Name == "S");
        r.Symbols.Should().Contain(s => s.Name == "E");
    }

    [Fact]
    public async Task NoSemantic_Fallback()
    {
        var code = "use std::collections::HashMap;\n// comment only\n";
        var r = await Analyze(code);
        r.Chunks.Should().NotBeEmpty(); // fallback sliding window
    }

    [Fact]
    public async Task MultiByte_Utf8()
    {
        var code = "// Привет\nmod тест { fn метод() { let s = \"Привет\"; } }";
        var r = await Analyze(code);
        RequireNative(r);
        var mod = ChunkFor(r, "тест");
        var fn = ChunkFor(r, "метод");
        mod.Source.Should().Contain("тест");
        fn.Source.Should().Contain("Привет");
        mod.Source.Should().NotContain("fn метод");
    }
}
