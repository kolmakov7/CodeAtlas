using CodeRag.Search.Models;

namespace CodeRag.Search;

public interface ISearchService
{
    Task<SearchResult> SearchCodeAsync(SearchRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<Symbol>> FindSymbolAsync(FindSymbolRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<Reference>> FindReferencesAsync(Guid symbolId, CancellationToken ct = default);

    Task<IReadOnlyList<Symbol>> FindImplementationsAsync(Guid symbolId, CancellationToken ct = default);

    Task<IReadOnlyList<Symbol>> FindCallersAsync(Guid symbolId, CancellationToken ct = default);

    Task<IReadOnlyList<Symbol>> FindCalleesAsync(Guid symbolId, CancellationToken ct = default);

    Task<SourceSlice> GetCodeAsync(GetCodeRequest req, CancellationToken ct = default);
}
