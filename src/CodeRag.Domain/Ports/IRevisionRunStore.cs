using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface IRevisionRunStore
{
    Task AddAsync(RevisionRun run, CancellationToken ct = default);
    Task UpdateAsync(RevisionRun run, CancellationToken ct = default);
    Task<RevisionRun?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<RevisionRun?> GetLatestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RevisionRun>> ListAsync(int limit = 20, CancellationToken ct = default);
}
