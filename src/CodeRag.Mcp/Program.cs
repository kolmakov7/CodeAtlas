using CodeRag.Analyzers.CSharp;
using CodeRag.Embeddings;
using CodeRag.Git.DependencyInjection;
using CodeRag.Indexer;
using CodeRag.Infrastructure.DependencyInjection;
using CodeRag.Infrastructure.Persistence;
using CodeRag.Mcp.Tools;
using CodeRag.Search;
using Microsoft.Extensions.Configuration;
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
        // Bind from Configuration (env + appsettings + settings table) instead of the no-arg
        // overload, which would fall back to the hardcoded EmbeddingOptions defaults.
        builder.Services.AddEmbeddings(builder.Configuration);
        builder.Services.AddIndexer(builder.Configuration);
        builder.Services.AddSearch(builder.Configuration);
        builder.Services.AddAnalyzers();

        builder.Services.AddSingleton<CodeSearchTools>();
        builder.Services.AddSingleton<IMcpToolHandler>(sp => sp.GetRequiredService<CodeSearchTools>());

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Таблица settings → IConfiguration: значения из БД имеют приоритет над env/appsettings.
        // Без этого MCP использовал бы defaults из EmbeddingOptions (bge-m3 / jina-embeddings-v5-text-small).
        // .NET 10: builder.Configuration — это ConfigurationManager без IConfiguration.Add,
        // поэтому источник добавляем в его Sources. Загрузка самой таблицы происходит в
        // InitializeDatabaseAsync() (FireReload) — после создания таблиц.
        IServiceProvider? settingsServiceProvider = null;
        var settingsTableProvider = new SettingsTableConfigurationProvider(
            () => settingsServiceProvider ?? throw new InvalidOperationException("ServiceProvider not ready"),
            () => settingsServiceProvider?.GetService<ILogger<SettingsTableConfigurationProvider>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsTableConfigurationProvider>.Instance);
        builder.Configuration.Sources.Add(new SettingsTableConfigurationSource(settingsTableProvider));
        builder.Services.AddSingleton(settingsTableProvider);

        var host = builder.Build();
        settingsServiceProvider = host.Services;

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
