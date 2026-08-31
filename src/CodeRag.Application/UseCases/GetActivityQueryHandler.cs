using CodeRag.Application.Ports;
using CodeRag.Domain.Ports;

namespace CodeRag.Application.UseCases;

public sealed class GetActivityQueryHandler : IActivityQueryHandler
{
    private readonly IActivityRepository _repo;
    public GetActivityQueryHandler(IActivityRepository repo) => _repo = repo;
    public Task<IReadOnlyList<ActivityDto>> HandleAsync(int limit, CancellationToken ct = default)
        => _repo.ListAsync(Math.Clamp(limit, 1, 100), ct);
}
