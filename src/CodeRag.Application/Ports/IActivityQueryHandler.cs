using CodeRag.Domain.Ports;

namespace CodeRag.Application.Ports;

public interface IActivityQueryHandler
{
    Task<IReadOnlyList<ActivityDto>> HandleAsync(int limit, CancellationToken ct = default);
}
