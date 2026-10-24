using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IEntityLinkStore
{
    Task AddAsync(EntityLink link, CancellationToken ct = default);
    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);
    Task<bool> RemoveAsync(
        string fromType, Guid fromId, string toType, Guid toId, string relation, CancellationToken ct = default);
    Task<IReadOnlyList<EntityLink>> GetOutgoingAsync(string fromType, Guid fromId, CancellationToken ct = default);
    Task<IReadOnlyList<EntityLink>> GetIncomingAsync(string toType, Guid toId, CancellationToken ct = default);
    Task<IReadOnlyList<EntityLink>> ListAsync(
        string? fromType = null,
        Guid? fromId = null,
        string? toType = null,
        Guid? toId = null,
        string? relation = null,
        int limit = 200,
        CancellationToken ct = default);
}
