using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IEntityHistoryStore
{
    Task AddAsync(EntityHistoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<EntityHistoryEntry>> ListAsync(
        string entityType, Guid entityId, int limit = 100, CancellationToken ct = default);
}
