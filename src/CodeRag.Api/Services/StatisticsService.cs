using CodeRag.Api.Models;
using CodeRag.Domain.Ports;

namespace CodeRag.Api.Services;

public sealed class StatisticsService
{
    private readonly IStatisticsQueryService _query;
    public StatisticsService(IStatisticsQueryService query) => _query = query;

    public async Task<RepositoryStatisticsDto> GetRepositoryStatisticsAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var s = await _query.GetRepositoryStatisticsAsync(repositoryId, ct).ConfigureAwait(false);
        return MapRepository(s);
    }

    public async Task<GlobalStatisticsDto> GetGlobalStatisticsAsync(CancellationToken ct = default)
    {
        var g = await _query.GetGlobalStatisticsAsync(ct).ConfigureAwait(false);
        var repoStats = g.Repositories.Select(MapRepository).ToList();
        return new GlobalStatisticsDto(g.RepositoryCount, g.FileCount, g.SymbolCount, g.ChunkCount,
            g.RelationCount, MapRelations(g.RelationsByType), g.JobCount,
            g.EmbeddingStats.Select(MapDomain).ToList(), g.EmbeddingsFailed, repoStats);
    }

    private static EmbeddingDomainStatsDto MapDomain(CodeRag.Domain.Ports.EmbeddingDomainStats d) => new(d.Domain, d.Indexed, d.Pending);

    private static IReadOnlyList<RelationTypeStatsDto> MapRelations(IReadOnlyList<CodeRag.Domain.Ports.RelationTypeStats> in_) =>
        in_.Select(r => new RelationTypeStatsDto(r.RelationType, r.Count)).ToList();

    private static RepositoryStatisticsDto MapRepository(CodeRag.Domain.Ports.RepositoryStatistics s) => new(
        s.RepositoryId, s.Name, s.FileCount, s.SymbolCount, s.ChunkCount, s.VectorCount,
        s.RelationCount, MapRelations(s.RelationsByType),
        s.EmbeddingStats.Select(d => new EmbeddingDomainStatsDto(d.Domain, d.Indexed, d.Pending)).ToList(),
        s.EmbeddingsFailed, s.IndexedCommit, s.CurrentCommit, s.Status);
}
