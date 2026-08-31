using CodeRag.Domain.Entities;

namespace CodeRag.Domain.Ports;

public interface ISymbolStore
{
    Task UpsertAsync(Symbol symbol, CancellationToken ct = default);
    Task UpsertBatchAsync(IEnumerable<Symbol> symbols, CancellationToken ct = default);
    Task<Symbol?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Symbol>> GetByFileAsync(Guid fileId, CancellationToken ct = default);
    Task<IReadOnlyList<Symbol>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
    Task DeleteByFileAsync(Guid fileId, CancellationToken ct = default);
    Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
}
