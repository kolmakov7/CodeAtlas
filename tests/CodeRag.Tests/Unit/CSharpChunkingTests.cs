using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.CSharp;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Regression tests for the C# semantic chunking model:
///  - method/constructor → ONE chunk: namespace + type declaration chain + full method source;
///  - class/interface/record/struct → skeleton (declaration + fields + signatures, no method bodies);
///  - oversized methods → budget-sized windows with [continued] header and correct line ranges;
///  - top-level statements → a single "Program" chunk with a symbol;
///  - directive-only files → no chunks.
/// </summary>
public sealed class CSharpChunkingTests
{
    private static async Task<AnalyzeResult> AnalyzeAsync(string code)
        => await new CSharpAnalyzer().AnalyzeAsync(
            new FileContext(Guid.NewGuid(), null, "Sample.cs", code, SupportedLanguages.CSharp),
            CancellationToken.None);

    private static List<Chunk> ChunksOf(AnalyzeResult r, string symbolName)
    {
        var ids = r.Symbols.Where(s => s.Name == symbolName).Select(s => s.Id).ToHashSet();
        return r.Chunks.Where(c => c.SymbolId.HasValue && ids.Contains(c.SymbolId.Value)).ToList();
    }

    /// <summary>
    /// Single-file fixtures routinely produce Roslyn *semantic* diagnostics (unresolved types,
    /// top-level-statements-needs-executable). Those are unrelated to chunking — only fail on
    /// chunk/walk failures.
    /// </summary>
    private static void AssertNoChunkingErrors(AnalyzeResult r)
    {
        r.Errors.Should().NotContain(
            e => e.Message.Contains("Chunk failed") || e.Message.Contains("Visit failed") || e.Message.Contains("Top-level chunk failed"),
            "the chunker itself must not fail: {0}",
            string.Join("; ", r.Errors.Where(e => e.Message.Contains("Chunk failed") || e.Message.Contains("Visit failed") || e.Message.Contains("Top-level chunk failed")).Select(e => e.Message)));
    }

    [Fact]
    public async Task Method_Chunk_Has_Context_And_Full_Body_Without_Neighbor_Body()
    {
        var code = """
            namespace App
            {
                public class OrderService
                {
                    private readonly ILogger _logger;

                    public OrderService(ILogger logger)
                    {
                        _logger = logger;
                    }

                    public int AddItem(int orderId, int itemId)
                    {
                        var token = "BODY_MARKER_ADD";
                        return orderId + itemId;
                    }

                    public void RemoveItem(int orderId)
                    {
                        var token = "BODY_MARKER_REMOVE";
                    }
                }
            }
            """;
        var r = await AnalyzeAsync(code);
        AssertNoChunkingErrors(r);

        var add = ChunksOf(r, "AddItem");
        add.Should().HaveCount(1, "a method that fits the budget must be exactly one chunk");
        var text = add[0].Source;

        text.Should().Contain("namespace App;");
        text.Should().Contain("class OrderService");
        text.Should().Contain("AddItem");
        text.Should().Contain("BODY_MARKER_ADD");
        text.Should().NotContain("BODY_MARKER_REMOVE", "a method chunk must not leak neighboring method bodies");
    }

    [Fact]
    public async Task Class_Chunk_Is_Skeleton_Without_Method_Bodies()
    {
        var code = """
            namespace App
            {
                public class Catalog
                {
                    public string Name { get; set; }
                    private int _count;

                    public int Count()
                    {
                        var token = "COUNT_MARKER_BODY";
                        return _count;
                    }

                    public void Reset()
                    {
                        var token = "RESET_MARKER_BODY";
                        _count = 0;
                    }
                }
            }
            """;
        var r = await AnalyzeAsync(code);
        r.Errors.Should().BeEmpty(string.Join("; ", r.Errors.Select(e => e.Message)));

        var classChunks = ChunksOf(r, "Catalog");
        classChunks.Should().ContainSingle();
        var skeleton = classChunks[0].Source;

        skeleton.Should().Contain("class Catalog");
        skeleton.Should().Contain("public string Name { get; set; }");
        skeleton.Should().Contain("private int _count;");
        skeleton.Should().Contain("public int Count()");
        skeleton.Should().Contain("public void Reset()");
        skeleton.Should().NotContain("COUNT_MARKER_BODY", "class skeleton must not embed method bodies");
        skeleton.Should().NotContain("RESET_MARKER_BODY", "class skeleton must not embed method bodies");
    }

    [Fact]
    public async Task Overloads_Are_Separate_Chunks()
    {
        var code = """
            namespace App
            {
                public class Dup
                {
                    public int Go(int x)
                    {
                        var token = "OVERLOAD_MARKER_A";
                        return x;
                    }

                    public int Go(string x)
                    {
                        var token = "OVERLOAD_MARKER_B";
                        return x.Length;
                    }
                }
            }
            """;
        var r = await AnalyzeAsync(code);
        var go = ChunksOf(r, "Go");
        go.Should().HaveCount(2, "each overload gets its own chunk");
        go.Single(c => c.Source.Contains("OVERLOAD_MARKER_A")).Source.Should().NotContain("OVERLOAD_MARKER_B");
        go.Single(c => c.Source.Contains("OVERLOAD_MARKER_B")).Source.Should().NotContain("OVERLOAD_MARKER_A");
    }

    [Fact]
    public async Task Large_Method_Splits_Into_Budget_Windows_With_Context_And_Correct_Lines()
    {
        var body = string.Join("\n",
            Enumerable.Range(1, 80).Select(i => $"        var filler{i} = \"FILLER_{i}\"; // padding padding padding {i}"));
        var code = $$"""
            namespace App
            {
                public class Big
                {
                    public int HugeMethod(int input)
                    {
            {{body}}
                        return input;
                    }
                }
            }
            """;
        var r = await AnalyzeAsync(code);
        r.Errors.Should().BeEmpty(string.Join("; ", r.Errors.Select(e => e.Message)));

        var chunks = ChunksOf(r, "HugeMethod");
        chunks.Count.Should().BeGreaterThanOrEqualTo(3, "a ~4.5KB method must be split across budget windows");

        var first = chunks[0];
        first.Source.Should().Contain("namespace App;");
        first.Source.Should().Contain("class Big");
        first.Source.Should().Contain("HugeMethod");

        chunks[1].Source.Should().Contain("// [continued]", "continuation windows must carry the context header");
        chunks[1].Source.Should().Contain("Sample.cs");

        // Line ranges must be real and monotonic (regression: split windows used to all report start==end)
        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].StartLine.Should().BeLessThanOrEqualTo(chunks[i].EndLine);
            if (i > 0)
                chunks[i].StartLine.Should().BeGreaterThan(chunks[i - 1].StartLine);
        }
        var pairs = chunks.Select(c => (c.StartLine, c.EndLine)).Distinct().Count();
        pairs.Should().Be(chunks.Count, "each window must have a distinct line range");
        chunks[^1].EndLine.Should().BeGreaterThan(chunks[0].StartLine + 50, "the last window must reach the method end");

        // Budget respected (the indexer keeps a safety split, so allow small headroom)
        chunks.Should().OnlyContain(c => c.Source.Length <= 1500 + 128, "no chunk may blow the embedding budget");
    }

    [Fact]
    public async Task TopLevel_Statements_Produce_Program_Chunk_With_Symbol()
    {
        var code = """
            using System;

            var host = "APP_HOST_TOKEN";
            Console.WriteLine(host);
            """;
        var r = await AnalyzeAsync(code);
        AssertNoChunkingErrors(r);

        r.Symbols.Should().Contain(s => s.Name == "Program" && s.Kind == SymbolKind.Method);
        var chunks = ChunksOf(r, "Program");
        chunks.Should().ContainSingle();
        chunks[0].Source.Should().Contain("APP_HOST_TOKEN");
    }

    [Fact]
    public async Task Directive_Only_File_Produces_No_Chunks()
    {
        var code = "using System;\nusing System.Threading.Tasks;\n";
        var r = await AnalyzeAsync(code);
        r.Chunks.Should().BeEmpty("files with only using directives must not produce sliding-window chunks");
    }

    [Fact]
    public async Task Nested_Type_Method_Chunk_Contains_Whole_Type_Chain()
    {
        var code = """
            namespace App
            {
                public class Outer
                {
                    public class Inner
                    {
                        public int Deep(int x)
                        {
                            var token = "DEEP_MARKER";
                            return x + 1;
                        }
                    }
                }
            }
            """;
        var r = await AnalyzeAsync(code);
        r.Errors.Should().BeEmpty(string.Join("; ", r.Errors.Select(e => e.Message)));

        var deep = ChunksOf(r, "Deep").Should().ContainSingle().Subject;
        deep.Source.Should().Contain("namespace App;");
        deep.Source.Should().Contain("class Outer");
        deep.Source.Should().Contain("class Inner");
        deep.Source.Should().Contain("DEEP_MARKER");

        var outerSkeleton = ChunksOf(r, "Outer").Single().Source;
        outerSkeleton.Should().Contain("class Inner", "outer skeleton lists nested type declarations");
        outerSkeleton.Should().NotContain("DEEP_MARKER");
    }

    [Fact]
    public async Task Enum_Stays_Single_Chunk()
    {
        var code = """
            namespace App
            {
                public enum OrderStatus
                {
                    None = 0,
                    Placed = 1,
                    Shipped = 2
                }
            }
            """;
        var r = await AnalyzeAsync(code);
        var enumChunks = ChunksOf(r, "OrderStatus");
        enumChunks.Should().ContainSingle("an enum is one semantic unit");
        enumChunks[0].Source.Should().Contain("None = 0");
        enumChunks[0].Source.Should().Contain("Shipped = 2");
    }

    [Fact]
    public async Task All_Chunks_Are_NonEmpty_And_Within_Budget()
    {
        var code = """
            namespace App
            {
                public class Mixed
                {
                    public string? Name { get; set; }

                    public int A(int x)
                    {
                        return x;
                    }
                }

                public record Point(int X, int Y);
            }
            """;
        var r = await AnalyzeAsync(code);
        r.Errors.Should().BeEmpty(string.Join("; ", r.Errors.Select(e => e.Message)));
        r.Chunks.Should().NotBeEmpty();
        r.Chunks.Should().OnlyContain(
            c => !string.IsNullOrWhiteSpace(c.Source) && c.Source.Length <= 1500 + 128,
            "every chunk must be non-empty and within the embedding budget");
    }
}
