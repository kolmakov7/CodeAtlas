using CodeRag.Analyzers.Abstractions;
using CodeRag.Analyzers.Sql;
using FluentAssertions;
using Xunit;

namespace CodeRag.Tests.Unit;

public class SqlChunkTests
{
    private static FileContext Ctx(string path, string content)
        => new(Guid.NewGuid(), null, path, content, SupportedLanguages.Sql, 100000);

    private static Task<AnalyzeResult> AnalyzeSql(string sql)
        => new SqlAnalyzer().AnalyzeAsync(Ctx("t.sql", sql), default);

    [Fact]
    public async Task ChunkSource_Stays_Within_Statement_Boundaries()
    {
        var sql = """
            CREATE PROCEDURE dbo.ProcA
            AS
            BEGIN
                SELECT 1;
            END
            GO
            CREATE PROCEDURE dbo.ProcB
            AS
            BEGIN
                SELECT 2;
            END
            """;
        var r = await AnalyzeSql(sql);
        r.Errors.Should().BeEmpty();
        r.Symbols.Should().HaveCount(2);
        r.Chunks.Should().HaveCount(2);

        var sa = r.Symbols.Single(s => s.Name == "ProcA");
        var sb = r.Symbols.Single(s => s.Name == "ProcB");
        var ca = r.Chunks.Single(c => c.SymbolId == sa.Id);
        var cb = r.Chunks.Single(c => c.SymbolId == sb.Id);

        ca.Source.Should().StartWith("CREATE PROCEDURE dbo.ProcA");
        ca.Source.Should().EndWith("END");
        ca.Source.Should().Contain("SELECT 1;");
        ca.Source.Should().NotContain("dbo.ProcB");
        ca.Source.Should().NotContain("GO");

        cb.Source.Should().StartWith("CREATE PROCEDURE dbo.ProcB");
        cb.Source.Should().Contain("SELECT 2;");
        cb.Source.Should().NotContain("SELECT 1;");
        cb.Source.Should().NotContain("dbo.ProcA");
    }

    [Fact]
    public async Task SymbolRange_Covers_Fragment_Exactly()
    {
        var sql = """
            CREATE TABLE dbo.T (Id INT);
            """;
        var r = await AnalyzeSql(sql);
        r.Errors.Should().BeEmpty();
        var sym = r.Symbols.Single(s => s.Name == "T");
        sym.StartLine.Should().Be(1);
        sym.EndLine.Should().Be(1);
        // "CREATE TABLE dbo.T (Id INT);" is 28 chars — EndColumn is 1-based exclusive
        sym.EndColumn.Should().Be(29);
        var chunk = r.Chunks.Single(c => c.SymbolId == sym.Id);
        chunk.Source.Should().Be("CREATE TABLE dbo.T (Id INT);");
    }

    [Fact]
    public async Task Multiple_ObjectKinds_Each_Get_Own_Chunk()
    {
        var sql = """
            CREATE PROCEDURE dbo.P1
            AS
            BEGIN
                SELECT 1;
            END
            GO
            CREATE VIEW dbo.V1 AS SELECT * FROM dbo.T;
            GO
            CREATE TABLE dbo.T2 (Id INT);
            """;
        var r = await AnalyzeSql(sql);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Procedure && s.Name == "P1");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.View && s.Name == "V1");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Table && s.Name == "T2");
        r.Chunks.Should().HaveCount(3);

        var viewChunk = r.Chunks.Single(c => c.SymbolId == r.Symbols.Single(s => s.Name == "V1").Id);
        viewChunk.Source.Should().StartWith("CREATE VIEW dbo.V1");
        viewChunk.Source.Should().EndWith("dbo.T;");
        viewChunk.Source.Should().NotContain("PROCEDURE");
        viewChunk.Source.Should().NotContain("CREATE TABLE");
    }

    [Fact]
    public async Task MultiByteUtf8_SourceIntact()
    {
        var sql = """
            CREATE PROCEDURE dbo.Регистрация -- 注册
            AS
            BEGIN
                SELECT N'привет мир', N'日本語';
            END
            GO
            CREATE PROCEDURE dbo.Second
            AS
            BEGIN
                SELECT 1;
            END
            """;
        var r = await AnalyzeSql(sql);
        r.Errors.Should().BeEmpty();
        r.Symbols.Should().HaveCount(2);
        var first = r.Chunks.First(c => c.SymbolId == r.Symbols.First(s => s.Name == "Регистрация").Id);
        first.Source.Should().Contain("注册");
        first.Source.Should().Contain("привет мир");
        first.Source.Should().Contain("日本語");
        var second = r.Chunks.First(c => c.SymbolId == r.Symbols.First(s => s.Name == "Second").Id);
        second.Source.Should().NotContain("注册");
        second.Source.Should().NotContain("привет");
    }

    [Fact]
    public async Task CreateOrAlter_Statements_Are_Captured()
    {
        var sql = """
            CREATE OR ALTER PROCEDURE dbo.RecalcTotals
            AS
            BEGIN
                UPDATE dbo.T SET Id = Id + 1;
            END
            GO
            create or alter function dbo.fnX(@v INT) RETURNS INT AS BEGIN RETURN @v + 1; END
            GO
            CREATE or ALTER TRIGGER dbo.trg ON dbo.T AFTER INSERT AS BEGIN SELECT 1; END
            """;
        var r = await AnalyzeSql(sql);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Procedure && s.Name == "RecalcTotals");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Function && s.Name == "fnX");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Trigger && s.Name == "trg");
        r.Chunks.Should().HaveCount(3);
        var procChunk = r.Chunks.Single(c => c.SymbolId == r.Symbols.Single(s => s.Name == "RecalcTotals").Id);
        procChunk.Source.Should().StartWith("CREATE OR ALTER PROCEDURE dbo.RecalcTotals");
        procChunk.Source.Should().NotContain("fnX");
    }

    [Fact]
    public async Task NestedCreate_Inside_Procedure_Is_Excluded_From_Parent_Chunk()
    {
        var sql = """
            CREATE PROCEDURE dbo.ETL
            AS
            BEGIN
                CREATE TABLE #tmp (Id INT);
                INSERT INTO #tmp VALUES (1);
            END
            """;
        var r = await AnalyzeSql(sql);
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Procedure && s.Name == "ETL");
        r.Symbols.Should().Contain(s => s.Kind == SymbolKind.Table && s.Name == "#tmp");
        r.Chunks.Should().HaveCount(2);

        var procSym = r.Symbols.Single(s => s.Name == "ETL");
        var tableSym = r.Symbols.Single(s => s.Name == "#tmp");
        var procChunk = r.Chunks.Single(c => c.SymbolId == procSym.Id);
        var tableChunk = r.Chunks.Single(c => c.SymbolId == tableSym.Id);

        // child keeps full content; parent excludes the child's range
        tableChunk.Source.Should().Be("CREATE TABLE #tmp (Id INT);");
        procChunk.Source.Should().StartWith("CREATE PROCEDURE dbo.ETL");
        procChunk.Source.Should().NotContain("CREATE TABLE #tmp");
        procChunk.Source.Should().Contain("INSERT INTO #tmp VALUES (1);");

        // proc Contains #tmp
        r.Relations.Should().Contain(rel => rel.RelationType == RelationType.Contains
            && rel.FromSymbolId == procSym.Id && rel.ToSymbolId == tableSym.Id);
    }

    [Fact]
    public async Task NoCreateStatements_FallsBackToSlidingWindow()
    {
        var sql = """
            SET NOCOUNT ON;
            SELECT * FROM Orders;
            """;
        var r = await AnalyzeSql(sql);
        r.Symbols.Should().BeEmpty();
        r.Chunks.Should().NotBeEmpty();
    }
}
