using CodeRag.Domain.Ports;

namespace CodeRag.Application.UseCases;

public sealed class GetStatisticsQueryHandler
{
    private readonly IStatisticsQueryService _svc;
    public GetStatisticsQueryHandler(IStatisticsQueryService svc) => _svc = svc;
    public Task<RepositoryStatistics> GetRepositoryAsync(Guid id, CancellationToken ct) => _svc.GetRepositoryStatisticsAsync(id, ct);
    public Task<GlobalStatistics> GetGlobalAsync(CancellationToken ct) => _svc.GetGlobalStatisticsAsync(ct);
}
