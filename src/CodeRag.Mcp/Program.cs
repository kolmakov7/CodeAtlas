using CodeRag.Analyzers.CSharp;
using CodeRag.Embeddings;
using CodeRag.Git.DependencyInjection;
using CodeRag.Indexer;
using CodeRag.Infrastructure.DependencyInjection;
using CodeRag.Mcp.Tools;
using CodeRag.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeRag.Mcp;

public sealed class McpProgram
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var dataDir = Environment.GetEnvironmentVariable("CODERAG__DataDir")
            ?? builder.Configuration["CODERAG:DataDir"]
            ?? builder.Configuration["CODERAG__DataDir"]
            ?? builder.Configuration["DataDir"]
            ?? "/data";
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "keys"));

        builder.Services.AddInfrastructure(dataDir);
        builder.Services.AddGit();
        builder.Services.AddEmbeddings();
        builder.Services.AddIndexer();
        builder.Services.AddSearch();
        builder.Services.AddAnalyzers();

        builder.Services.AddSingleton<CodeSearchTools>();
        builder.Services.AddSingleton<IMcpToolHandler>(sp => sp.GetRequiredService<CodeSearchTools>());

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var host = builder.Build();

        using (var scope = host.Services.CreateScope())
        {
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("CodeRag.Mcp");
            try
            {
                await scope.ServiceProvider.InitializeDatabaseAsync().ConfigureAwait(false);
                logger.LogInformation("MCP initialized DB at {DataDir}", dataDir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MCP failed to initialize database");
            }
        }

        await host.RunAsync();
    }
}
