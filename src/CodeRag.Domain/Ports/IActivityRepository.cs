using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public sealed record ActivityDto(
    string Id,
    string At,
    string Actor,
    string Action,
    string? RepositoryId,
    string? Detail);

public interface IActivityRepository
{
    Task<IReadOnlyList<ActivityDto>> ListAsync(int limit, CancellationToken ct = default);
    Task AddAsync(ActivityLog entry, CancellationToken ct = default);
}
