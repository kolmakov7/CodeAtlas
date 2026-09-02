namespace CodeRag.Domain.Ports;

/// <summary>One MCP tool invocation (for usage statistics).</summary>
public sealed record McpToolUsage(
    DateTimeOffset At,
    string Tool,
    bool Success,
    int? DurationMs,
    Guid? RepositoryId);

/// <summary>Per-tool aggregate.</summary>
public sealed record McpToolUsageTotal(string Tool, long Total, long Errors);

/// <summary>Per-day aggregate (for time-series charts).</summary>
public sealed record McpDailyUsage(string Day, long Total);

/// <summary>Full usage stats payload.</summary>
public sealed record McpUsageStats(
    long TotalCalls,
    long TotalErrors,
    int Days,
    IReadOnlyList<McpToolUsageTotal> ByTool,
    IReadOnlyList<McpDailyUsage> ByDay);

/// <summary>Records MCP tool invocations (fire-and-forget, never blocks callers).</summary>
public interface IMcpUsageRecorder
{
    void Record(string tool, bool success, int? durationMs, Guid? repositoryId = null);
}

/// <summary>Reads MCP usage statistics.</summary>
public interface IMcpUsageQueryService
{
    Task<McpUsageStats> GetStatsAsync(int days = 30, CancellationToken ct = default);
}
