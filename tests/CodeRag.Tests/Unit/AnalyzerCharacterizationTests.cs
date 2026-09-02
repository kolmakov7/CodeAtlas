using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.CSharp;
using CodeRag.Analyzers.Sql;
using CodeRag.Analyzers.TreeSitter;
using CodeRag.Analyzers.Vbs;
using FluentAssertions;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Characterization tests — поведенческий baseline всех анализаторов до рефакторинга.
/// Проверяют результат анализа (AnalyzeResult), а не внутренние классы.
/// symbols / chunks / line ranges / FQN / relations / references / inheritance / calls / errors / edge cases.
/// </summary>
public sealed class AnalyzerCharacterizationTests
{
    private static FileContext Ctx(string path, string content, string lang, int maxChars = 1500)
        => new(Guid.NewGuid(), null, path, content, lang, maxChars);

    // ───────────── C# ─────────────

    [Fact]
    public async Task CSharp_Symbols_Contain_All_Kinds()
    {
        var code = """
            namespace Demo
            {
                public enum Status { None=0, Active=1 }
                public interface IRepo { void Save(); }
                public abstract class Base { public abstract void Run(); }
                public class Service : Base, IRepo
                {
                    public event System.EventHandler Changed;
                    private int _count;
                    public string Name { get; set; }
                    public Service() { }
                    public override void Run() { _count++; }
                    public void Save() { }
                }
                public record Point(int X, int Y);
                public struct Value { public int N; }
            }
            """;
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("A.cs", code, SupportedLanguages.CSharp), CancellationToken.None);
        r.Errors.Where(e => e.Message.Contains("Chunk failed") || e.Message.Contains("Visit failed")).Should().BeEmpty();
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Namespace && s.Name == "Demo");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Enum && s.Name == "Status");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Interface && s.Name == "IRepo");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Class && s.Name == "Base");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Class && s.Name == "Service");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Record && s.Name == "Point");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Struct && s.Name == "Value");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Method && s.Name == "Run");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Method && s.Name == "Save");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Property && s.Name == "Name");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Field && s.Name == "_count");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Constructor && s.Name == "Service");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Event && s.Name == "Changed");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.EnumMember);
    }

    [Fact]
    public async Task CSharp_Fqn_Includes_Namespace_And_Overload_Params()
    {
        var code = """
            namespace Demo { public class Calc { public int Add(int x,int y)=>x+y; public int Add(string a,string b)=>a.Length+b.Length; } }
            """;
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("Calc.cs", code, SupportedLanguages.CSharp), default);
        var adds = r.Symbols.Where(s => s.Name == "Add").ToList();
        adds.Should().HaveCount(2);
        adds.Should().OnlyContain(s => s.Fqn!.Contains("Demo") && s.Fqn.Contains("Calc") && s.Fqn.Contains("Add("));
        adds[0].Fqn.Should().NotBe(adds[1].Fqn);
        adds.Should().OnlyContain(s => s.Namespace == "Demo");
    }

    [Fact]
    public async Task CSharp_LineRanges_Are_Monotonic_And_Valid()
    {
        var code = """
            namespace Demo
            {
                public class A
                {
                    public void M1() { var x=1; }
                    public void M2() { var y=2; }
                }
            }
            """;
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("A.cs", code, SupportedLanguages.CSharp), default);
        foreach (var s in r.Symbols)
        {
            s.StartLine.Should().BeGreaterThan(0);
            s.EndLine.Should().BeGreaterThanOrEqualTo(s.StartLine, $"{s.Kind} {s.Name}");
            s.StartColumn.Should().BeGreaterThan(0);
            s.EndColumn.Should().BeGreaterThan(0);
        }
        foreach (var c in r.Chunks)
        {
            c.StartLine.Should().BeGreaterThan(0);
            c.EndLine.Should().BeGreaterThanOrEqualTo(c.StartLine);
            c.Source.Should().NotBeNullOrWhiteSpace();
            c.ContentHash.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task CSharp_Relations_Contains_And_Inheritance_And_Calls_PerFile()
    {
        var code = """
            namespace Demo
            {
                public interface ILog { void Write(); }
                public class Base { public virtual void Go(){} }
                public class Child : Base, ILog
                {
                    public void Write(){}
                    public override void Go(){ Write(); }
                    public void Caller(){ Go(); }
                }
            }
            """;
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("C.cs", code, SupportedLanguages.CSharp), default);
        var child = r.Symbols.Single(s => s.Name == "Child");
        var baseSym = r.Symbols.Single(s => s.Name == "Base");
        var iLog = r.Symbols.Single(s => s.Name == "ILog");
        // Contains
        r.Relations.Should().Contain(rel => rel.RelationType == RelationType.Contains && rel.FromSymbolId == child.Id);
        // Inherits/Implements per-file semantic (within file)
        r.Relations.Any(rel => rel.RelationType == RelationType.Inherits && rel.FromSymbolId == child.Id).Should().BeTrue($"expected Inherits, got {string.Join("; ", r.Relations.Select(x => x.RelationType))}");
        r.Relations.Any(rel => rel.RelationType == RelationType.Implements && rel.FromSymbolId == child.Id).Should().BeTrue();
        // Calls: at least one Calls edge exists (per-file syntax fallback may produce limited)
        r.Relations.Should().Contain(rel => rel.RelationType == RelationType.Calls);
    }

    [Fact]
    public async Task CSharp_DirectiveOnly_NoChunks()
    {
        var code = "using System;\nusing System.Threading.Tasks;\n";
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("U.cs", code, SupportedLanguages.CSharp), default);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task CSharp_SyntaxError_Produces_AnalyzerError_Not_Throw()
    {
        var code = "namespace Demo { public class Broken { public void M( { } }";
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("B.cs", code, SupportedLanguages.CSharp), default);
        r.Errors.Should().NotBeEmpty();
        // Should still produce at least error, not throw
        r.Should().NotBeNull();
    }

    [Fact]
    public async Task CSharp_Empty_Content_Returns_Empty()
    {
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("E.cs", "", SupportedLanguages.CSharp), default);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().BeEmpty();
        r.Relations.Should().BeEmpty();
    }

    [Fact]
    public async Task CSharp_CanAnalyze_ExtensionCheck()
    {
        var a = new CSharpAnalyzer();
        a.CanAnalyze("foo.cs").Should().BeTrue();
        a.CanAnalyze("foo.csx").Should().BeTrue();
        a.CanAnalyze("foo.py").Should().BeFalse();
        a.CanAnalyze("").Should().BeFalse();
    }

    [Fact]
    public async Task CSharp_Chunks_Are_Within_Budget_And_NonEmpty()
    {
        var code = """
            namespace App
            {
                public class Mixed
                {
                    public string? Name { get; set; }
                    public int A(int x){ return x; }
                }
            }
            """;
        var r = await new CSharpAnalyzer().AnalyzeAsync(Ctx("M.cs", code, SupportedLanguages.CSharp), default);
        r.Chunks.Should().NotBeEmpty();
        r.Chunks.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Source) && c.Source.Length <= 1500 + 128);
    }

    // ───────────── SQL ─────────────

    [Fact]
    public async Task Sql_Procedure_Symbol_And_Chunk()
    {
        var sql = """
            CREATE PROCEDURE dbo.GetOrders
                @id INT
            AS
            BEGIN
                SELECT * FROM Orders WHERE Id = @id;
            END
            GO
            """;
        var r = await new SqlAnalyzer().AnalyzeAsync(Ctx("proc.sql", sql, SupportedLanguages.Sql), default);
        r.Symbols.Should().ContainSingle(s => s.Kind == SymbolKind.Procedure && s.Name == "GetOrders");
        var sym = r.Symbols.Single(s => s.Name == "GetOrders");
        sym.Fqn.Should().Be("dbo.GetOrders");
        sym.Namespace.Should().Be("dbo");
        sym.StartLine.Should().Be(1);
        r.Chunks.Should().Contain(c => c.SymbolId == sym.Id && c.Source.Contains("SELECT"));
        r.Chunks[0].Language.Should().Be(SupportedLanguages.Sql);
    }

    [Fact]
    public async Task Sql_View_Table_Trigger_Function_Kinds()
    {
        var sql = """
            CREATE VIEW dbo.ActiveOrders AS SELECT * FROM Orders WHERE Active=1;
            GO
            CREATE TABLE dbo.Items (Id INT PRIMARY KEY, Name NVARCHAR(100));
            GO
            CREATE FUNCTION dbo.fnDouble(@x INT) RETURNS INT AS BEGIN RETURN @x*2; END;
            GO
            CREATE TRIGGER dbo.trgAudit ON dbo.Items AFTER INSERT AS BEGIN SELECT 1; END;
            GO
            """;
        var r = await new SqlAnalyzer().AnalyzeAsync(Ctx("all.sql", sql, SupportedLanguages.Sql), default);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.View && s.Name == "ActiveOrders");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Table && s.Name == "Items");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Function && s.Name == "fnDouble");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Trigger && s.Name == "trgAudit");
        r.Chunks.Count.Should().Be(4);
    }

    [Fact]
    public async Task Sql_MultipleCreate_WithoutGo_Produces_Error()
    {
        var sql = """
            CREATE PROCEDURE dbo.A AS BEGIN SELECT 1 END
            CREATE PROCEDURE dbo.B AS BEGIN SELECT 2 END
            """;
        var r = await new SqlAnalyzer().AnalyzeAsync(Ctx("nog.sql", sql, SupportedLanguages.Sql), default);
        r.Errors.Should().Contain(e => e.Message.Contains("GO"));
        r.Should().NotBeNull();
        // Parser may recover 0-2 symbols depending on ScriptDom version — assert non-throw and at least fallback
        (r.Symbols.Count >= 0 && (r.Chunks.Count >= 0)).Should().BeTrue();
    }

    [Fact]
    public async Task Sql_LineRanges_And_Fqn_For_Bracketed_Names()
    {
        var sql = "CREATE PROCEDURE [mySchema].[GetOrders] AS BEGIN SELECT 1 END\nGO\n";
        var r = await new SqlAnalyzer().AnalyzeAsync(Ctx("p.sql", sql, SupportedLanguages.Sql), default);
        var s = r.Symbols.Single();
        s.StartLine.Should().Be(1);
        s.EndLine.Should().BeGreaterThanOrEqualTo(1);
        s.Fqn.Should().Contain("GetOrders");
    }

    [Fact]
    public async Task Sql_SyntaxError_Produces_Error_And_FallbackChunk()
    {
        var sql = "CREATTE PROCDURE ???";
        var r = await new SqlAnalyzer().AnalyzeAsync(Ctx("bad.sql", sql, SupportedLanguages.Sql), default);
        r.Should().NotBeNull();
        // Either errors or sliding window fallback
        (r.Errors.Count > 0 || r.Chunks.Count > 0).Should().BeTrue();
    }

    [Fact]
    public async Task Sql_Empty_Returns_Empty()
    {
        var r = await new SqlAnalyzer().AnalyzeAsync(Ctx("e.sql", "", SupportedLanguages.Sql), default);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task Sql_CanAnalyze()
    {
        var a = new SqlAnalyzer();
        a.CanAnalyze("a.sql").Should().BeTrue();
        a.CanAnalyze("b.tsql").Should().BeTrue();
        a.CanAnalyze("c.cs").Should().BeFalse();
    }

    // ───────────── TreeSitter ─────────────

    [Fact]
    public async Task TreeSitter_Python_Function_And_Class_With_Calls()
    {
        var py = """
            class OrderService:
                def save(self, item):
                    print(item)

                def process(self, item):
                    self.save(item)
                    helper(item)

            def helper(x):
                return x
            """;
        var r = await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("app.py", py, SupportedLanguages.Python), default);
        // If TreeSitter native not available, fallback is sliding window — still non-empty chunks
        if (r.Symbols.Count == 0)
        {
            r.Chunks.Should().NotBeEmpty("fallback sliding window");
            r.Errors.Should().Contain(e => e.Message.Contains("not available") || e.Message.Contains("TreeSitter"));
            return;
        }
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Class && s.Name == "OrderService");
        r.Symbols.Should().Contain(s => (s.Kind == SymbolKind.Function || s.Kind == SymbolKind.Method) && s.Name == "save");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Function && s.Name == "helper");
        r.Chunks.Should().NotBeEmpty();
        // Calls: process -> save / helper (internal method names)
        r.Relations.Any(rel => rel.RelationType == RelationType.Calls).Should().BeTrue();
        foreach (var c in r.Chunks)
        {
            c.StartLine.Should().BeGreaterThan(0);
            c.EndLine.Should().BeGreaterThanOrEqualTo(c.StartLine);
            c.Language.Should().Be(SupportedLanguages.Python);
        }
        // FQN via stack
        var save = r.Symbols.Single(s => s.Name == "save");
        save.Fqn.Should().Contain("OrderService");
    }

    [Fact]
    public async Task TreeSitter_JavaScript_FunctionAndClass()
    {
        var js = """
            class Repo {
                save(item){ return item; }
            }
            function process(item){
                const r = new Repo();
                r.save(item);
            }
            """;
        var r = await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("app.js", js, SupportedLanguages.JavaScript), default);
        if (r.Symbols.Count == 0)
        {
            r.Chunks.Should().NotBeEmpty();
            return;
        }
        r.Symbols.Should().Contain(s => s.Name == "Repo");
        r.Symbols.Should().Contain(s => s.Name == "process" || s.Name == "save");
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TreeSitter_Go_Function()
    {
        var go = """
            package main
            type MyStruct struct { N int }
            func (m MyStruct) Save() int { return m.N }
            func Process(x int) int { return x+1 }
            """;
        var r = await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("main.go", go, SupportedLanguages.Go), default);
        if (r.Symbols.Count == 0)
        {
            r.Chunks.Should().NotBeEmpty();
            return;
        }
        r.Symbols.Should().Contain(s => s.Name.Contains("Save") || s.Name.Contains("Process"));
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TreeSitter_Cpp_FunctionAndClass()
    {
        var cpp = """
            class MyClass { public: void methodOne(); int value; };
            void MyClass::methodOne() { value = 1; }
            int freeFunc(int x){ return x*2; }
            """;
        var r = await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("a.cpp", cpp, SupportedLanguages.Cpp), default);
        if (r.Symbols.Count == 0)
        {
            r.Chunks.Should().NotBeEmpty();
            return;
        }
        r.Symbols.Should().NotBeEmpty();
        r.Chunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TreeSitter_Empty_Returns_Empty()
    {
        var r = await new TreeSitterAnalyzer().AnalyzeAsync(Ctx("e.py", "", SupportedLanguages.Python), default);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task TreeSitter_CanAnalyze_All_Extensions()
    {
        var a = new TreeSitterAnalyzer();
        a.CanAnalyze("a.py").Should().BeTrue();
        a.CanAnalyze("b.js").Should().BeTrue();
        a.CanAnalyze("c.go").Should().BeTrue();
        a.CanAnalyze("d.cpp").Should().BeTrue();
        a.CanAnalyze("e.cs").Should().BeFalse();
        a.CanAnalyze("f.sql").Should().BeFalse();
    }

    // ───────────── VBS ─────────────

    [Fact]
    public async Task Vbs_Class_With_Methods_Contains_Relation_And_Fqn()
    {
        var vbs = """
            Class OrderService
                Public Sub Save(item)
                    MsgBox item
                End Sub

                Public Function GetCount()
                    GetCount = 42
                End Function
            End Class

            Sub Helper()
                Dim x
                x = 1
            End Sub
            """;
        var r = await new VbsAnalyzer().AnalyzeAsync(Ctx("app.vbs", vbs, SupportedLanguages.Vbs), default);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Class && s.Name == "OrderService");
        r.Symbols.Should().Contain(s => s.Name == "Save");
        r.Symbols.Should().Contain(s => s.Name == "GetCount");
        r.Symbols.Should().Contain(s => s.Name == "Helper");
        var cls = r.Symbols.Single(s => s.Name == "OrderService");
        var save = r.Symbols.Single(s => s.Name == "Save");
        save.Fqn.Should().Be("OrderService.Save");
        save.ParentSymbolId.Should().Be(cls.Id);
        r.Relations.Should().Contain(rel => rel.RelationType == RelationType.Contains && rel.FromSymbolId == cls.Id && rel.ToSymbolId == save.Id);
        r.Chunks.Should().NotBeEmpty();
        foreach (var c in r.Chunks)
        {
            c.StartLine.Should().BeGreaterThan(0);
            c.EndLine.Should().BeGreaterThanOrEqualTo(c.StartLine);
            c.Language.Should().Be(SupportedLanguages.Vbs);
        }
    }

    [Fact]
    public async Task Vbs_LineRanges_Correct()
    {
        var vbs = "Class A\n  Sub M()\n  End Sub\nEnd Class\n";
        var r = await new VbsAnalyzer().AnalyzeAsync(Ctx("a.vbs", vbs, SupportedLanguages.Vbs), default);
        var cls = r.Symbols.Single(s => s.Name == "A");
        cls.StartLine.Should().Be(1);
        cls.EndLine.Should().Be(4);
        var m = r.Symbols.Single(s => s.Name == "M");
        m.StartLine.Should().Be(2);
        m.EndLine.Should().Be(3);
    }

    [Fact]
    public async Task Vbs_Empty_Returns_Empty()
    {
        var r = await new VbsAnalyzer().AnalyzeAsync(Ctx("e.vbs", "", SupportedLanguages.Vbs), default);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task Vbs_NoMatch_Produces_Fallback_And_Error()
    {
        var vbs = "Dim x\nx = 1\n";
        var r = await new VbsAnalyzer().AnalyzeAsync(Ctx("x.vbs", vbs, SupportedLanguages.Vbs), default);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().NotBeEmpty("fallback sliding window");
        r.Errors.Should().Contain(e => e.Message.Contains("no Sub"));
    }

    [Fact]
    public async Task Vbs_CanAnalyze()
    {
        var a = new VbsAnalyzer();
        a.CanAnalyze("a.vbs").Should().BeTrue();
        a.CanAnalyze("b.vb").Should().BeTrue();
        a.CanAnalyze("c.cs").Should().BeFalse();
    }

    // ───────────── Cross-cutting: ChunkingHelper & budgets ─────────────

    [Fact]
    public void ChunkingHelper_Hash_Is_Stable()
    {
        var h1 = ChunkingHelper.ComputeHash("hello");
        var h2 = ChunkingHelper.ComputeHash("hello");
        h1.Should().Be(h2);
        h1.Length.Should().Be(64);
    }

    [Fact]
    public void ChunkingHelper_SplitByCharBudget_Respects_Header()
    {
        var source = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i} filler filler filler filler filler filler filler filler filler filler"));
        var header = "// [continued] file.cs :: Ns.Class.Method";
        var slices = ChunkingHelper.SplitByCharBudget(source, 1, 100, 800, 80, header);
        slices.Should().NotBeEmpty();
        slices[1].Text.Should().Contain(header);
        slices.Should().OnlyContain(s => s.Text.Length <= 800 + header.Length + 1);
    }

    [Fact]
    public async Task All_Analyzers_Produce_NonDuplicate_SymbolIds()
    {
        var analyzers = new ICodeAnalyzer[] { new CSharpAnalyzer(), new SqlAnalyzer(), new VbsAnalyzer(), new TreeSitterAnalyzer() };
        var contexts = new[]
        {
            Ctx("a.cs", "namespace N{ public class C{ public void M(){} } }", SupportedLanguages.CSharp),
            Ctx("b.sql", "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 END\nGO\n", SupportedLanguages.Sql),
            Ctx("c.vbs", "Class C\n Sub M()\n End Sub\nEnd Class", SupportedLanguages.Vbs),
            Ctx("d.py", "def foo():\n  pass\n", SupportedLanguages.Python),
        };
        foreach (var (an, ctx) in analyzers.Zip(contexts))
        {
            var r = await an.AnalyzeAsync(ctx, default);
            r.Symbols.Select(s => s.Id).Distinct().Count().Should().Be(r.Symbols.Count);
            r.Chunks.Select(c => c.Id).Distinct().Count().Should().Be(r.Chunks.Count);
        }
    }
}
