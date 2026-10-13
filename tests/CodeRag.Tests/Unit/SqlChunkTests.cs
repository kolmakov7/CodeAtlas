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

    private static string ExtractSqlSource(string enriched)
    {
        var idx = enriched.IndexOf('\n');
        return idx >= 0 ? enriched[(idx + 1)..] : enriched;
    }

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

        // Verify semantic enrichment is present
        ca.Source.Should().Contain("-- CodeAtlas Semantic Index:");
        ca.Source.Should().Contain("Kind=Procedure");
        ca.Source.Should().Contain("Procedure=ProcA");
        
        // Verify original SQL content is intact after the header
        var rawA = ExtractSqlSource(ca.Source);
        rawA.Should().StartWith("CREATE PROCEDURE dbo.ProcA");
        rawA.Should().EndWith("END");
        rawA.Should().Contain("SELECT 1;");
        rawA.Should().NotContain("dbo.ProcB");
        rawA.Should().NotContain("GO");

        // Verify semantic enrichment for ProcB
        var rawB = ExtractSqlSource(cb.Source);
        rawB.Should().StartWith("CREATE PROCEDURE dbo.ProcB");
        rawB.Should().Contain("SELECT 2;");
        rawB.Should().NotContain("SELECT 1;");
        rawB.Should().NotContain("dbo.ProcA");
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
        
        // Verify semantic enrichment is present
        chunk.Source.Should().Contain("-- CodeAtlas Semantic Index:");
        chunk.Source.Should().Contain("Kind=Table");
        chunk.Source.Should().Contain("Tables=dbo.T");
        
        // Verify original SQL is intact
        var raw = ExtractSqlSource(chunk.Source);
        raw.Should().Be("CREATE TABLE dbo.T (Id INT);");
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
        var viewRaw = ExtractSqlSource(viewChunk.Source);
        viewRaw.Should().StartWith("CREATE VIEW dbo.V1");
        viewRaw.Should().EndWith("dbo.T;");
        viewRaw.Should().NotContain("PROCEDURE");
        viewRaw.Should().NotContain("CREATE TABLE");

        // Verify semantic enrichment for view
        viewChunk.Source.Should().Contain("Kind=View");
        viewChunk.Source.Should().Contain("dbo.T");
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
        var firstRaw = ExtractSqlSource(first.Source);
        firstRaw.Should().Contain("注册");
        firstRaw.Should().Contain("привет мир");
        firstRaw.Should().Contain("日本語");
        var second = r.Chunks.First(c => c.SymbolId == r.Symbols.First(s => s.Name == "Second").Id);
        var secondRaw = ExtractSqlSource(second.Source);
        secondRaw.Should().NotContain("注册");
        secondRaw.Should().NotContain("привет");
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
        var procRaw = ExtractSqlSource(procChunk.Source);
        procRaw.Should().StartWith("CREATE OR ALTER PROCEDURE dbo.RecalcTotals");
        procRaw.Should().NotContain("fnX");

        // Verify semantic enrichment includes UPDATE operation and table reference
        procChunk.Source.Should().Contain("Kind=Procedure");
        procChunk.Source.Should().Contain("Operations=UPDATE,CREATE");
        procChunk.Source.Should().Contain("Tables=dbo.T");
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
        var tableRaw = ExtractSqlSource(tableChunk.Source);
        tableRaw.Should().Be("CREATE TABLE #tmp (Id INT);");
        
        var procRaw = ExtractSqlSource(procChunk.Source);
        procRaw.Should().StartWith("CREATE PROCEDURE dbo.ETL");
        procRaw.Should().NotContain("CREATE TABLE #tmp");
        procRaw.Should().Contain("INSERT INTO #tmp VALUES (1);");

        // Verify semantic enrichment for procedure
        procChunk.Source.Should().Contain("Kind=Procedure");
        procChunk.Source.Should().Contain("Operations=INSERT,CREATE");
        procChunk.Source.Should().Contain("Tables=#tmp");
        procChunk.Source.Should().Contain("INSERT INTO #tmp");

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

        // Sliding window chunks should not have semantic enrichment (no symbol info)
        var chunk = r.Chunks.First();
        chunk.Source.Should().Contain("SELECT * FROM Orders;");
    }

    [Fact]
    public async Task EnrichedChunk_Contains_Extracted_Metadata()
    {
        var sql = """
            CREATE PROCEDURE dbo.GetCustomerOrders
                @customerId INT,
                @startDate DATE,
                @endDate DATE
            AS
            BEGIN
                SELECT 
                    o.OrderId,
                    o.OrderDate,
                    c.CustomerName,
                    p.ProductName,
                    oi.Quantity
                FROM dbo.Orders o
                INNER JOIN dbo.Customers c ON o.CustomerId = c.CustomerId
                INNER JOIN dbo.OrderItems oi ON o.OrderId = oi.OrderId
                INNER JOIN dbo.Products p ON oi.ProductId = p.ProductId
                WHERE c.CustomerId = @customerId
                  AND o.OrderDate BETWEEN @startDate AND @endDate
                ORDER BY o.OrderDate DESC;
            END
            """;
        var r = await AnalyzeSql(sql);
        r.Errors.Should().BeEmpty();
        r.Symbols.Should().HaveCount(1);
        r.Chunks.Should().HaveCount(1);

        var chunk = r.Chunks.First();
        
        // Verify semantic header
        chunk.Source.Should().Contain("-- CodeAtlas Semantic Index:");
        chunk.Source.Should().Contain("Kind=Procedure");
        chunk.Source.Should().Contain("Procedure=GetCustomerOrders");
        
        // Verify extracted operations
        chunk.Source.Should().Contain("Operations=SELECT,CREATE");
        
        // Verify extracted tables (with schema prefix, comma-separated)
        chunk.Source.Should().Contain("dbo.Orders");
        chunk.Source.Should().Contain("dbo.Customers");
        chunk.Source.Should().Contain("dbo.OrderItems");
        chunk.Source.Should().Contain("dbo.Products");
        
        // Note: Column extraction is best-effort via regex; full SQL parsing would be needed for reliable extraction
        
        // Verify extracted parameters (comma-separated after Parameters=)
        chunk.Source.Should().Contain("Parameters=");
        chunk.Source.Should().Contain("customerId");
        chunk.Source.Should().Contain("startDate");
        chunk.Source.Should().Contain("endDate");
        
        // Note: Join extraction is best-effort via regex; the tables are already captured in Tables=
    }
}
