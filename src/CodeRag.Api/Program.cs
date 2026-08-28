using CodeRag.Analyzers.CSharp;
using CodeRag.Api.Services;
using CodeRag.Embeddings;
using CodeRag.Git.DependencyInjection;
using CodeRag.Indexer;
using CodeRag.Infrastructure.DependencyInjection;
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
builder.Services.AddInfrastructure(dataDir);
builder.Services.AddGit();
builder.Services.AddEmbeddings();
builder.Services.AddIndexer();
builder.Services.AddSearch();
builder.Services.AddAnalyzers();

// Api services
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<StatisticsService>();

var app = builder.Build();

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
app.MapControllers();

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
