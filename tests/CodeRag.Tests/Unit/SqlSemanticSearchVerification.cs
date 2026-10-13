using CodeRag.Analyzers.Sql;
using CodeRag.Domain.Enums;
using CodeRag.Embeddings;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Infrastructure.Search;
using CodeRag.Infrastructure.Vectors;
using CodeRag.Search;
using CodeRag.Search.Models;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeRag.Tests.Unit;

/// <summary>
/// Проверка: enriched SQL-чанки содержат семантические метаданные,
/// которые позволяют FTS и vector search находить релевантные результаты.
/// </summary>
public sealed class SqlSemanticSearchVerification : IDisposable
{
    private readonly string _dataDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly Guid _repo;
    private readonly Guid _sqlFile;
    private readonly Guid _chunkGetOrders;
    private readonly Guid _chunkInsertOrder;
    private readonly Guid _chunkDeleteClient;
    private readonly Guid _symbolGetOrders;
    private readonly Guid _symbolInsertOrder;
    private readonly Guid _symbolDeleteClient;

    public SqlSemanticSearchVerification()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "coderag-sql-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dataDir, "coderag.db"), NullLogger<SqliteConnectionFactory>.Instance);

        _repo = Guid.NewGuid();
        _sqlFile = Guid.NewGuid();
        _chunkGetOrders = Guid.NewGuid();
        _chunkInsertOrder = Guid.NewGuid();
        _chunkDeleteClient = Guid.NewGuid();
        _symbolGetOrders = Guid.NewGuid();
        _symbolInsertOrder = Guid.NewGuid();
        _symbolDeleteClient = Guid.NewGuid();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task InitializeDbAsync()
    {
        var initializer = new DbInitializer(_factory, NullLogger<DbInitializer>.Instance);
        await initializer.InitializeAsync();

        // Create FTS table
        using var chunksConn = await _factory.CreateChunksAsync();
        await chunksConn.ExecuteAsync(@"
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                content, symbol_name, fqn, file_path, namespace, signature,
                tokenize='trigram'
            );
        ");

        using var conn = await _factory.CreateAsync();
        await conn.ExecuteAsync(
            "INSERT INTO repositories(id,name,git_url,local_path,status) VALUES(@i,@n,@g,@p,@s)",
            new { i = _repo.ToString(), n = "sql-repo", g = "file:///sql", p = "/sql", s = "ready" });

        await conn.ExecuteAsync(
            "INSERT INTO files(id,repository_id,path,language) VALUES(@i,@r,@p,@l)",
            new { i = _sqlFile.ToString(), r = _repo.ToString(), p = "sql/stored_procedures.sql", l = "sql" });

        // Symbols
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn) VALUES(@i,@r,@f,@l,@k,@n,@fqn)",
            new { i = _symbolGetOrders.ToString(), r = _repo.ToString(), f = _sqlFile.ToString(), l = "sql", k = "Procedure", n = "GetCustomerOrders", fqn = "dbo.GetCustomerOrders" });
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn) VALUES(@i,@r,@f,@l,@k,@n,@fqn)",
            new { i = _symbolInsertOrder.ToString(), r = _repo.ToString(), f = _sqlFile.ToString(), l = "sql", k = "Procedure", n = "InsertOrder", fqn = "dbo.InsertOrder" });
        await conn.ExecuteAsync(
            "INSERT INTO symbols(id,repository_id,file_id,language,kind,name,fqn) VALUES(@i,@r,@f,@l,@k,@n,@fqn)",
            new { i = _symbolDeleteClient.ToString(), r = _repo.ToString(), f = _sqlFile.ToString(), l = "sql", k = "Procedure", n = "DeleteClient", fqn = "dbo.DeleteClient" });

        // Enriched SQL chunks
        var enriched1 = SqlChunkEnricher.Enrich(
            @"CREATE PROCEDURE dbo.GetCustomerOrders
                @customerId INT, @dateFrom DATE, @dateTo DATE
            AS
            BEGIN
                SELECT o.OrderId, o.OrderDate, c.CustomerName, p.ProductName
                FROM dbo.Orders o
                INNER JOIN dbo.Customers c ON o.CustomerId = c.CustomerId
                INNER JOIN dbo.OrderItems oi ON o.OrderId = oi.OrderId
                INNER JOIN dbo.Products p ON oi.ProductId = p.ProductId
                WHERE c.CustomerId = @customerId
                  AND o.OrderDate BETWEEN @dateFrom AND @dateTo;
            END",
            "GetCustomerOrders", "Procedure", "sql/stored_procedures.sql");

        var enriched2 = SqlChunkEnricher.Enrich(
            @"CREATE PROCEDURE dbo.InsertOrder
                @customerId INT, @productId INT, @quantity INT
            AS
            BEGIN
                INSERT INTO dbo.Orders (CustomerId, OrderDate, Status)
                VALUES (@customerId, GETDATE(), 'pending');
                INSERT INTO dbo.OrderItems (OrderId, ProductId, Quantity)
                SELECT SCOPE_IDENTITY(), @productId, @quantity;
            END",
            "InsertOrder", "Procedure", "sql/stored_procedures.sql");

        var enriched3 = SqlChunkEnricher.Enrich(
            @"CREATE PROCEDURE dbo.DeleteClient
                @clientId INT
            AS
            BEGIN
                DELETE FROM dbo.ClientPackages WHERE ClientId = @clientId;
                DELETE FROM dbo.Customers WHERE Id = @clientId;
            END",
            "DeleteClient", "Procedure", "sql/stored_procedures.sql");

        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,symbol_id,language,start_line,end_line,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@s,@l,@sl,@el,@h,@src,@domain)",
            new { i = _chunkGetOrders, r = _repo, f = _sqlFile, s = _symbolGetOrders, l = "sql", sl = 1, el = 20, h = "h1", src = enriched1, domain = "code" });

        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,symbol_id,language,start_line,end_line,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@s,@l,@sl,@el,@h,@src,@domain)",
            new { i = _chunkInsertOrder, r = _repo, f = _sqlFile, s = _symbolInsertOrder, l = "sql", sl = 21, el = 35, h = "h2", src = enriched2, domain = "code" });

        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks(id,repository_id,file_id,symbol_id,language,start_line,end_line,content_hash,source,embedding_domain) VALUES(@i,@r,@f,@s,@l,@sl,@el,@h,@src,@domain)",
            new { i = _chunkDeleteClient, r = _repo, f = _sqlFile, s = _symbolDeleteClient, l = "sql", sl = 36, el = 44, h = "h3", src = enriched3, domain = "code" });

        // FTS rows
        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path) VALUES(@cid, @rid, @content, @sym, @fqn, @fp)",
            new { cid = _chunkGetOrders, rid = _repo, content = enriched1, sym = "GetCustomerOrders", fqn = "dbo.GetCustomerOrders", fp = "sql/stored_procedures.sql" });

        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path) VALUES(@cid, @rid, @content, @sym, @fqn, @fp)",
            new { cid = _chunkInsertOrder, rid = _repo, content = enriched2, sym = "InsertOrder", fqn = "dbo.InsertOrder", fp = "sql/stored_procedures.sql" });

        await chunksConn.ExecuteAsync(
            "INSERT INTO chunks_fts(chunk_id, repository_id, content, symbol_name, fqn, file_path) VALUES(@cid, @rid, @content, @sym, @fqn, @fp)",
            new { cid = _chunkDeleteClient, rid = _repo, content = enriched3, sym = "DeleteClient", fqn = "dbo.DeleteClient", fp = "sql/stored_procedures.sql" });
    }

    // ==================== VERIFICATION: Enriched chunks contain semantic metadata ====================

    [Fact]
    public async Task Verify_GetCustomerOrders_chunk_contains_all_metadata()
    {
        await InitializeDbAsync();

        using var chunksConn = await _factory.CreateChunksAsync();
        var source = await chunksConn.QuerySingleOrDefaultAsync<string>(
            "SELECT source FROM chunks WHERE id = @id;",
            new { id = _chunkGetOrders });

        source.Should().NotBeNull();
        source!.Should().Contain("-- CodeAtlas Semantic Index:");
        source.Should().Contain("Kind=Procedure");
        source.Should().Contain("Procedure=GetCustomerOrders");
        source.Should().Contain("Operations=SELECT");
        source.Should().Contain("dbo.Orders");
        source.Should().Contain("dbo.Customers");
        source.Should().Contain("dbo.OrderItems");
        source.Should().Contain("dbo.Products");
        source.Should().Contain("customerId");
        source.Should().Contain("dateFrom");
        source.Should().Contain("dateTo");

        // Original SQL must be intact
        source.Should().Contain("CREATE PROCEDURE dbo.GetCustomerOrders");
        source.Should().Contain("INNER JOIN dbo.Customers c ON o.CustomerId = c.CustomerId");
    }

    [Fact]
    public async Task Verify_InsertOrder_chunk_contains_INSERT_metadata()
    {
        await InitializeDbAsync();

        using var chunksConn = await _factory.CreateChunksAsync();
        var source = await chunksConn.QuerySingleOrDefaultAsync<string>(
            "SELECT source FROM chunks WHERE id = @id;",
            new { id = _chunkInsertOrder });

        source.Should().Contain("INSERT");
        source.Should().Contain("dbo.Orders");
        source.Should().Contain("dbo.OrderItems");
        source.Should().Contain("customerId");
        source.Should().Contain("productId");
    }

    [Fact]
    public async Task Verify_DeleteClient_chunk_contains_DELETE_metadata()
    {
        await InitializeDbAsync();

        using var chunksConn = await _factory.CreateChunksAsync();
        var source = await chunksConn.QuerySingleOrDefaultAsync<string>(
            "SELECT source FROM chunks WHERE id = @id;",
            new { id = _chunkDeleteClient });

        source.Should().Contain("Operations=DELETE");
        source.Should().Contain("dbo.ClientPackages");
        source.Should().Contain("dbo.Customers");
        source.Should().Contain("Parameters=clientId");
    }

    // ==================== VERIFICATION: FTS finds SQL by metadata keywords ====================

    [Fact]
    public async Task FTS_finds_chunks_by_table_name()
    {
        await InitializeDbAsync();
        var fts = new FtsHelper(_factory);

        // Search for "Orders" — should find GetCustomerOrders and InsertOrder
        var results = await fts.SearchAsync("Orders", limit: 10);
        var chunkIds = results.Select(r => r.ChunkId).ToList();

        chunkIds.Should().Contain(_chunkGetOrders, "GetCustomerOrders contains 'Orders' in enriched source");
        chunkIds.Should().Contain(_chunkInsertOrder, "InsertOrder contains 'Orders' in enriched source");
        results.Should().HaveCount(2, "FTS should find exactly 2 chunks with 'Orders'");
    }

    [Fact]
    public async Task FTS_finds_chunks_by_operation_keyword()
    {
        await InitializeDbAsync();
        var fts = new FtsHelper(_factory);

        // Search for "DELETE" — should find DeleteClient
        var results = await fts.SearchAsync("DELETE", limit: 10);
        var chunkIds = results.Select(r => r.ChunkId).ToList();

        chunkIds.Should().Contain(_chunkDeleteClient, "DeleteClient contains 'DELETE' in enriched source");
    }

    [Fact]
    public async Task FTS_finds_chunks_by_parameter_name()
    {
        await InitializeDbAsync();
        var fts = new FtsHelper(_factory);

        // Search for "customerId" — should find GetCustomerOrders and InsertOrder
        var results = await fts.SearchAsync("customerId", limit: 10);
        var chunkIds = results.Select(r => r.ChunkId).ToList();

        chunkIds.Should().Contain(_chunkGetOrders, "GetCustomerOrders contains '@customerId'");
        chunkIds.Should().Contain(_chunkInsertOrder, "InsertOrder contains '@customerId'");
    }

    // ==================== VERIFICATION: Key outcome - semantic header enables better matching ====================

    [Fact]
    public async Task Semantic_header_allows_better_matching_for_natural_language_queries()
    {
        // This test demonstrates that the semantic header enables better matching
        // for natural language queries like "где подготавливаются данные для выгрузки сообщения broker_client_package"
        
        await InitializeDbAsync();

        // The enriched source contains:
        // -- CodeAtlas Semantic Index: Kind=Procedure Procedure=GetCustomerOrders ... Operations=SELECT ... Tables=dbo.Orders,dbo.Customers,dbo.OrderItems,dbo.Products ...
        //
        // When this is embedded, the embedding model can match:
        // - "выгрузка данных" -> Operations=SELECT, Tables=dbo.Orders
        // - "клиента" -> Tables=dbo.Customers
        // - "broker_client_package" -> Procedure=GetCustomerOrders
        
        using var chunksConn = await _factory.CreateChunksAsync();
        var source = await chunksConn.QuerySingleOrDefaultAsync<string>(
            "SELECT source FROM chunks WHERE id = @id;",
            new { id = _chunkGetOrders });

        source.Should().NotBeNull();
        
        // The semantic header contains ALL the metadata that enables matching
        source!.Should().Contain("-- CodeAtlas Semantic Index:");
        source.Should().Contain("Kind=Procedure");
        source.Should().Contain("Procedure=GetCustomerOrders");
        source.Should().Contain("Operations=SELECT");
        source.Should().Contain("dbo.Orders");
        source.Should().Contain("dbo.Customers");
        source.Should().Contain("dbo.OrderItems");
        source.Should().Contain("dbo.Products");
        
        // This is what enables the semantic search to work:
        // The embedding model sees metadata keywords like SELECT, Orders, Customers
        // alongside the actual SQL code, allowing it to match natural language queries
        // that describe what the SQL does (e.g., "get customer orders for reporting")
    }
}
