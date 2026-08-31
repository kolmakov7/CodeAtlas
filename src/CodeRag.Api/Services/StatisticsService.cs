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
        return new RepositoryStatisticsDto(s.RepositoryId, s.Name, s.FileCount, s.SymbolCount, s.ChunkCount, s.VectorCount, s.IndexedCommit, s.CurrentCommit, s.Status);
    }

    public async Task<GlobalStatisticsDto> GetGlobalStatisticsAsync(CancellationToken ct = default)
    {
        var g = await _query.GetGlobalStatisticsAsync(ct).ConfigureAwait(false);
        var repoStats = g.Repositories.Select(s => new RepositoryStatisticsDto(s.RepositoryId, s.Name, s.FileCount, s.SymbolCount, s.ChunkCount, s.VectorCount, s.IndexedCommit, s.CurrentCommit, s.Status)).ToList();
        return new GlobalStatisticsDto(g.RepositoryCount, g.FileCount, g.SymbolCount, g.ChunkCount, g.JobCount, repoStats);
    }
}
