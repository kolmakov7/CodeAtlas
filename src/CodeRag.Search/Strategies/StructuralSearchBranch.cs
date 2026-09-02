using CodeRag.Domain.Ports;
using CodeRag.Search.Models;
using Microsoft.Extensions.Logging;

namespace CodeRag.Search.Strategies;

public sealed class StructuralSearchBranch : ISearchBranch
{
    public SearchOrigin Origin => SearchOrigin.Structural;
    private readonly ISymbolStore _symbols;
    private readonly IChunkStore _chunks;
    private readonly IRelationStore _relations;
    private readonly ILogger<StructuralSearchBranch> _logger;

    public StructuralSearchBranch(ISymbolStore symbols, IChunkStore chunks, IRelationStore relations, ILogger<StructuralSearchBranch> logger)
    {
        _symbols = symbols;
        _chunks = chunks;
        _relations = relations;
        _logger = logger;
    }

    public bool ShouldRun(SearchMode mode) => mode == SearchMode.Hybrid;

    public async Task<int> ContributeAsync(SearchBranchContext ctx, Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct)
    {
        try
        {
            var structChunkIds = await GetStructuralCandidatesAsync(ctx.Query, ctx.ExactSymbolIds, ctx.RepoFilter, ct).ConfigureAwait(false);
            foreach (var cid in structChunkIds)
            {
                if (!candidates.TryGetValue(cid, out var acc))
                {
                    acc = new CandidateAccumulator(cid);
                    candidates[cid] = acc;
                }
                acc.Origins.Add(Origin);
                acc.HasStructural = true;
            }
            return structChunkIds.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Structural branch failed query={Query}", ctx.Query);
            return 0;
        }
    }

    private async Task<List<Guid>> GetStructuralCandidatesAsync(string query, List<Guid> exactSymbolIds, Guid[]? repoIds, CancellationToken ct)
    {
        var relatedChunkIds = new HashSet<Guid>();
        if (exactSymbolIds.Count > 0)
        {
            var rels = await _relations.QueryEndpointsBySymbolIdsAsync(exactSymbolIds, ct).ConfigureAwait(false);
            var relatedSymbolIds = new HashSet<Guid>(exactSymbolIds);
            foreach (var r in rels)
            {
                if (!exactSymbolIds.Contains(r.FromSymbolId)) relatedSymbolIds.Add(r.FromSymbolId);
                if (!exactSymbolIds.Contains(r.ToSymbolId)) relatedSymbolIds.Add(r.ToSymbolId);
            }
            var relatedOnly = relatedSymbolIds.Where(g => !exactSymbolIds.Contains(g)).ToList();
            if (relatedOnly.Count > 0)
            {
                foreach (var cid in await _chunks.GetChunkIdsBySymbolIdsAsync(relatedOnly, ct).ConfigureAwait(false)) relatedChunkIds.Add(cid);
            }
        }
        try
        {
            var likeSymbolIds = (await _symbols.FindLikeIdsAsync(query, repoIds, 20, ct).ConfigureAwait(false)).ToList();
            var newIds = likeSymbolIds.Where(id => !exactSymbolIds.Contains(id)).ToList();
            if (newIds.Count > 0)
            {
                foreach (var cid in await _chunks.GetChunkIdsBySymbolIdsAsync(newIds, ct).ConfigureAwait(false)) relatedChunkIds.Add(cid);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Structural LIKE branch failed query={Query}", query); }
        return relatedChunkIds.ToList();
    }
}
