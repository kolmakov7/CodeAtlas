using CodeRag.Domain.Entities;
using CodeRag.Domain.Models;

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

    /// <summary>Exact match on fqn or name, optionally filtered by repositories/languages/kind.</summary>
    Task<IReadOnlyCollection<Guid>> FindExactIdsAsync(
        string name,
        Guid[]? repositoryIds = null,
        string[]? languages = null,
        string? kind = null,
        CancellationToken ct = default);

    /// <summary>Case-insensitive LIKE match on name or fqn, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyCollection<Guid>> FindLikeIdsAsync(
        string term,
        Guid[]? repositoryIds = null,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>Search projection for the given symbol ids (symbol + file path).</summary>
    Task<IReadOnlyList<SymbolInfo>> GetSearchInfoByIdsAsync(IReadOnlyCollection<Guid> symbolIds, CancellationToken ct = default);

    /// <summary>Paged LIKE search over name/fqn/namespace, ordered by exact match then kind priority.</summary>
    Task<IReadOnlyList<SymbolInfo>> SearchAsync(
        string term,
        Guid? repositoryId = null,
        string? language = null,
        string? kind = null,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default);
}
