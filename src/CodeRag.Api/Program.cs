using CodeRag.Analyzers.CSharp;
using CodeRag.Api.Services;
using CodeRag.Application.Ports;
using CodeRag.Application.UseCases;
using CodeRag.Domain.Ports;
using CodeRag.Embeddings;
using CodeRag.Git.DependencyInjection;
using CodeRag.Indexer;
using CodeRag.Infrastructure.DependencyInjection;
using CodeRag.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using CodeRag.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Config: /data settings optional - support env CODERAG__DataDir via Configuration with ':' separator or direct env var
var dataDir = Environment.GetEnvironmentVariable("CODERAG__DataDir")
    ?? builder.Configuration["CODERAG:DataDir"]
    ?? builder.Configuration["CODERAG__DataDir"]
    ?? builder.Configuration["DataDir"]
    ?? "/data";
Directory.CreateDirectory(dataDir);
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);

// DataProtection keys /data/keys
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("CodeRag");

// Logging without secrets: ensure no DP secrets in logs - use default filter
builder.Logging.AddConsole();

// CORS
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Controllers + Swagger
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "CodeRag API", Version = "v1", Description = "CodeRAG REST API Phase 6" });
});

// Infrastructure wiring
builder.Services.AddHttpClient();
builder.Services.AddInfrastructure(builder.Configuration, dataDir);
builder.Services.AddGit();
// Embeddings: single bind — Configuration normalizes "__" to ":" automatically
builder.Services.AddEmbeddings(builder.Configuration);
// Бинд опций из конфигурации (включая таблицу settings — hot reload через IOptionsMonitor)
builder.Services.AddIndexer(builder.Configuration);
builder.Services.AddSearch(builder.Configuration);
builder.Services.AddAnalyzers();

// Application layer handlers (CQRS-lite)
builder.Services.AddSingleton<IActivityQueryHandler, GetActivityQueryHandler>();
builder.Services.AddSingleton<GetStatisticsQueryHandler>();

// Api services — thin wrappers over Domain ports (no SQL inside)
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<StatisticsService>();

// MCP remote (Streamable HTTP) — reuse tools from CodeRag.Mcp, exposed at /mcp
// stdio не нужен, http достаточно для remote opencode (type: remote, url: http://host:8080/mcp)
builder.Services.AddSingleton<CodeRag.Mcp.Tools.CodeSearchTools>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(CodeRag.Mcp.Tools.CodeSearchTools).Assembly);

// Таблица settings → IConfiguration: значения из БД имеют приоритет над env/appsettings,
// hot reload через FireReload() после Upsert (SettingsController).
#pragma warning disable ASP0013 // источник добавляется после дефолтов; builder.Configuration (IConfiguration) Add не поддерживает
IServiceProvider? settingsServiceProvider = null;
var settingsTableProvider = new SettingsTableConfigurationProvider(
    () => settingsServiceProvider ?? throw new InvalidOperationException("ServiceProvider not ready"),
    () => settingsServiceProvider?.GetService<ILogger<SettingsTableConfigurationProvider>>()
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsTableConfigurationProvider>.Instance);
builder.Host.ConfigureAppConfiguration((_, config) =>
    config.Add(new SettingsTableConfigurationSource(settingsTableProvider)));
#pragma warning restore ASP0013
builder.Services.AddSingleton(settingsTableProvider);

var app = builder.Build();
settingsServiceProvider = app.Services;

// Ensure DB initialized
using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider.InitializeDatabaseAsync().ConfigureAwait(false);
        // Trigger analyzer registration check
        var analyzers = scope.ServiceProvider.GetServices<CodeRag.Analyzers.Abstractions.ICodeAnalyzer>();
        var loggerInit = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        loggerInit.LogInformation("Initialized DB at {DataDir}, analyzers={Count}", dataDir, analyzers.Count());
        // Загрузить сохранённые настройки из БД в IConfiguration (первый Load() при Build()
        // выполняется до инициализации DI и БД, поэтому значения тогда ещё недоступны).
        settingsTableProvider.FireReload();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to initialize database");
    }
}

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "CodeRag v1"));

app.MapGet("/openapi/{documentName}.json", (string documentName, Swashbuckle.AspNetCore.Swagger.ISwaggerProvider provider) =>
{
    try { var swagger = provider.GetSwagger(documentName); return Results.Json(swagger); } catch { return Results.NotFound(); }
});

app.UseCors();

// MCP Streamable HTTP — remote для opencode (type: remote, url: http://host:8080/mcp)
app.MapMcp("/mcp");

// Web UI static (React Vite dist → wwwroot) — MapStaticAssets on .NET9+/UseStaticFiles fallback
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.MapControllers();

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
