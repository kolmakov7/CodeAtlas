using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence.Repositories;

/// <summary>
/// Fire-and-forget recorder of MCP tool invocations. Never throws, never blocks:
/// a failed INSERT is logged at Debug and dropped.
/// </summary>
public sealed class DapperMcpUsageRecorder : IMcpUsageRecorder, IDisposable
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DapperMcpUsageRecorder> _logger;

    public DapperMcpUsageRecorder(IDbConnectionFactory factory, ILogger<DapperMcpUsageRecorder> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public void Record(string tool, bool success, int? durationMs, Guid? repositoryId = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var conn = await _factory.CreateAsync().ConfigureAwait(false);
                await conn.ExecuteAsync(
                    "INSERT INTO mcp_tool_usage(at, tool, status, duration_ms, repository_id) VALUES (@at, @tool, @status, @durationMs, @repositoryId);",
                    new
                    {
                        at = DateTimeOffset.UtcNow.ToString("o"),
                        tool,
                        status = success ? "ok" : "error",
                        durationMs,
                        repositoryId = repositoryId?.ToString()
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to record MCP usage for tool {Tool}", tool);
            }
        });
    }

    public void Dispose()
    {
        // Nothing to dispose: writes are detached tasks with best-effort semantics.
    }
}
