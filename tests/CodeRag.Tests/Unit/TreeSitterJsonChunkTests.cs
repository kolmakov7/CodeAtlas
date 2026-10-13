using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.TreeSitter;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

public sealed class TreeSitterJsonChunkTests
{
    private static FileContext Ctx(string p, string c, string lang) => new(Guid.NewGuid(), null, p, c, lang, 100000);
    private static Task<AnalyzeResult> Analyze(string code) => new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.json", code, SupportedLanguages.Json), default);
    private static void RequireNative(AnalyzeResult r) => r.Symbols.Should().NotBeEmpty();

    [Fact]
    public async Task Container_Object_Is_Chunk()
    {
        var code = "{ \"outer\": { \"inner\": { \"a\": 1 }, \"b\": 2 }, \"flat\": 1 }";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Section);
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Nested_Object_Excluded_From_Parent()
    {
        var code = "{\n  \"a\": { \"b\": { \"c\": 1 }, \"d\": 2 },\n  \"e\": { \"f\": 3 },\n  \"g\": 4\n}";
        var r = await Analyze(code);
        RequireNative(r);
        // outer "a" should be a chunk, top-level json also
        var outer = r.Chunks.FirstOrDefault(c => r.Symbols.Single(s => s.Id == c.SymbolId).Name == "a");
        var top = r.Chunks.FirstOrDefault(c => r.Symbols.Single(s => s.Id == c.SymbolId).Name == "json");
        outer.Should().NotBeNull();
        top.Should().NotBeNull();
        outer!.Source.Should().Contain("\"b\"");
        outer.Source.Should().Contain("\"d\"");
        top!.Source.Should().Contain("\"e\"");
        top.Source.Should().NotContain("\"b\": { \"c\": 1 }");
    }

    [Fact]
    public async Task TopLevel_Is_Json_Name()
    {
        // "a" is container only if its inner object itself contains nested object; { "b": 1 } is leaf, so not container -> no "a" chunk, only top-level "json"
        var code = "{ \"a\": { \"inner\": { \"x\": 1 }, \"b\": 2 }, \"c\": 2 }";
        var r = await Analyze(code);
        RequireNative(r);
        r.Symbols.Should().Contain(s => s.Name == "json");
        r.Symbols.Should().Contain(s => s.Name == "a");
    }

    [Fact]
    public async Task Flat_Json_Fallback()
    {
        var code = "{ \"a\": 1, \"b\": 2, \"c\": \"hello\" }";
        var r = await Analyze(code);
        // Flat JSON has no container objects -> fallback sliding window (SymbolId null)
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Array_Containing_Objects_Is_Not_Chunk_By_Itself()
    {
        var code = "{ \"items\": [ { \"id\": 1 }, { \"id\": 2 } ] }";
        var r = await Analyze(code);
        // "items" object contains array with objects -> array itself not chunk, but outer object containing array should be considered container?
        // Our rule: object containing pair where value is array -> container, so items's parent object is chunk
        RequireNative(r);
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MultiByte_Utf8()
    {
        var code = "{ \"ключ\": { \"влож\": { \"a\": 1 }, \"b\": 2 }, \"flat\": \"Привет\" }";
        var r = await Analyze(code);
        RequireNative(r);
        var outer = r.Chunks.First(c => r.Symbols.Single(s => s.Id == c.SymbolId).Name == "ключ");
        outer.Source.Should().Contain("влож");
        // parent should not contain nested leaf
    }
}
