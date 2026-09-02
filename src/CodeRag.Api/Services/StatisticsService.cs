using CodeRag.Api.Models;
using CodeRag.Domain.Ports;

namespace CodeRag.Api.Services;

public sealed class StatisticsService
{
    private readonly IStatisticsQueryService _query;
    private readonly IMcpUsageQueryService? _mcp;

    public StatisticsService(IStatisticsQueryService query, IMcpUsageQueryService? mcp = null)
    {
        _query = query;
        _mcp = mcp;
    }

    public async Task<RepositoryStatisticsDto> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var s = await _query.GetRepositoryStatisticsAsync(repositoryId, ct).ConfigureAwait(false);
        return MapRepository(s);
    }

    public async Task<GlobalStatisticsDto> GetGlobalStatisticsAsync(CancellationToken ct = default)
    {
        var g = await _query.GetGlobalStatisticsAsync(ct).ConfigureAwait(false);
        var repoStats = g.Repositories.Select(MapRepository).ToList();
        var storageSizes = await _query.GetStorageSizesAsync(ct).ConfigureAwait(false);
        var storage = new StorageDto(storageSizes.SqliteBytes, storageSizes.VectorBytes, storageSizes.ReposBytes);
        return new GlobalStatisticsDto(g.RepositoryCount, g.FileCount, g.SymbolCount, g.ChunkCount,
            g.RelationCount, MapRelations(g.RelationsByType), g.JobCount,
            g.EmbeddingStats.Select(MapDomain).ToList(), g.EmbeddingsFailed, repoStats, storage, g.LanguageBreakdown);
    }

    /// <summary>MCP tool-usage stats (aggregated from mcp_tool_usage).</summary>
    public async Task<McpUsageStatsDto> GetMcpUsageStatsAsync(int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        if (_mcp is null)
            return new McpUsageStatsDto(0, 0, days, Array.Empty<McpToolUsageDto>(), Array.Empty<McpDailyUsageDto>());
        var s = await _mcp.GetStatsAsync(days, ct).ConfigureAwait(false);
        return new McpUsageStatsDto(
            s.TotalCalls, s.TotalErrors, s.Days,
            s.ByTool.Select(t => new McpToolUsageDto(t.Tool, t.Total, t.Errors)).ToList(),
            s.ByDay.Select(d => new McpDailyUsageDto(d.Day, d.Total)).ToList());
    }

    private static EmbeddingDomainStatsDto MapDomain(CodeRag.Domain.Ports.EmbeddingDomainStats d) => new(d.Domain, d.Indexed, d.Pending);

    private static IReadOnlyList<RelationTypeStatsDto> MapRelations(IReadOnlyList<CodeRag.Domain.Ports.RelationTypeStats> in_) =>
        in_.Select(r => new RelationTypeStatsDto(r.RelationType, r.Count)).ToList();

    private static RepositoryStatisticsDto MapRepository(CodeRag.Domain.Ports.RepositoryStatistics s) => new(
        s.RepositoryId, s.Name, s.FileCount, s.SymbolCount, s.ChunkCount, s.VectorCount,
        s.RelationCount, MapRelations(s.RelationsByType),
        s.EmbeddingStats.Select(d => new EmbeddingDomainStatsDto(d.Domain, d.Indexed, d.Pending)).ToList(),
        s.EmbeddingsFailed, s.IndexedCommit, s.CurrentCommit, s.Status, s.LanguageBreakdown);
}
