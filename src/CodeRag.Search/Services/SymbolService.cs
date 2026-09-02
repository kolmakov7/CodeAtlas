using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using CodeRag.Search.Models;

namespace CodeRag.Search.Services;

public sealed class SymbolService
{
    private readonly ISymbolStore _store;
    public SymbolService(ISymbolStore store) => _store = store;

    public async Task<IReadOnlyList<Symbol>> FindAsync(FindSymbolRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query)) return Array.Empty<Symbol>();
        var limit = req.Limit <= 0 ? 20 : Math.Min(req.Limit, 100);
        var offset = Math.Max(0, req.Offset);
        var rows = await _store.SearchAsync(req.Query, req.RepositoryId, req.Language, req.SymbolKind, limit, offset, ct).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<Symbol?> GetAsync(Guid symbolId, CancellationToken ct)
    {
        if (symbolId == Guid.Empty) return null;
        var list = await LoadByIdsAsync(new[] { symbolId }, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    public async Task<IReadOnlyList<Symbol>> LoadByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<Symbol>();
        var rows = await _store.GetSearchInfoByIdsAsync(ids, ct).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    private static Symbol Map(SymbolInfo s) => new(s.Id, s.RepositoryId, s.FileId, s.Language, s.Kind, s.Name, s.Fqn, s.Namespace, s.Signature, s.StartLine, s.EndLine, s.FilePath);
}
