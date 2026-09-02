using CodeRag.Domain.Ports;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence.Repositories;

public sealed class DapperMcpUsageQueryService : IMcpUsageQueryService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DapperMcpUsageQueryService> _logger;

    public DapperMcpUsageQueryService(IDbConnectionFactory factory, ILogger<DapperMcpUsageQueryService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<McpUsageStats> GetStatsAsync(int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToString("o");

        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        var totals = await conn.QueryFirstAsync<TotalsRow>(
            "SELECT COUNT(*) AS total, COALESCE(SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END), 0) AS errors FROM mcp_tool_usage WHERE at >= @since;",
            new { since }).ConfigureAwait(false);

        var byTool = (await conn.QueryAsync<ToolRow>(
            """
            SELECT tool AS tool,
                   COUNT(*) AS total,
                   COALESCE(SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END), 0) AS errors
            FROM mcp_tool_usage
            WHERE at >= @since
            GROUP BY tool
            ORDER BY total DESC;
            """,
            new { since }).ConfigureAwait(false)).Select(r => new McpToolUsageTotal(r.tool, r.total, r.errors)).ToArray();

        // Day buckets: full range including empty days so the chart has a continuous axis.
        var byDayRows = (await conn.QueryAsync<DayRow>(
            """
            SELECT substr(at, 1, 10) AS day, COUNT(*) AS total
            FROM mcp_tool_usage
            WHERE at >= @since
            GROUP BY day;
            """,
            new { since }).ConfigureAwait(false)).ToDictionary(r => r.day, r => r.total);

        var byDay = new List<McpDailyUsage>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var day = DateTimeOffset.UtcNow.AddDays(-i).ToString("yyyy-MM-dd");
            byDay.Add(new McpDailyUsage(day, byDayRows.GetValueOrDefault(day, 0L)));
        }

        return new McpUsageStats(totals.total, totals.errors, days, byTool, byDay);
    }

    private sealed class TotalsRow
    {
        public long total { get; set; }
        public long errors { get; set; }
    }

    private sealed class ToolRow
    {
        public string tool { get; set; } = "";
        public long total { get; set; }
        public long errors { get; set; }
    }

    private sealed class DayRow
    {
        public string day { get; set; } = "";
        public long total { get; set; }
    }
}
