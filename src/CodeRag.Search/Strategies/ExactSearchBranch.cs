using CodeRag.Domain.Ports;
using CodeRag.Search.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Search.Strategies;

public sealed class ExactSearchBranch : ISearchBranch
{
    public SearchOrigin Origin => SearchOrigin.Exact;
    private readonly ISymbolStore _symbols;
    private readonly IChunkStore _chunks;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly ILogger<ExactSearchBranch> _logger;

    public ExactSearchBranch(ISymbolStore symbols, IChunkStore chunks, IOptionsMonitor<SearchOptions> options, ILogger<ExactSearchBranch> logger)
    {
        _symbols = symbols;
        _chunks = chunks;
        _options = options;
        _logger = logger;
    }

    public bool ShouldRun(SearchMode mode) => mode == SearchMode.Hybrid || mode == SearchMode.Symbol;

    public async Task<int> ContributeAsync(SearchBranchContext ctx, Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct)
    {
        try
        {
            var (chunkIds, symbolIds) = await GetExactCandidatesAsync(ctx.Query, ctx.RepoFilter, ctx.LangFilter, ctx.SymbolKindFilter, ct).ConfigureAwait(false);
            ctx.ExactSymbolIds = symbolIds;
            foreach (var cid in chunkIds)
            {
                if (!candidates.TryGetValue(cid, out var acc))
                {
                    acc = new CandidateAccumulator(cid);
                    candidates[cid] = acc;
                }
                acc.Origins.Add(Origin);
                acc.HasExact = true;
                acc.ExactBonus = _options.CurrentValue.ExactBonus;
            }
            return chunkIds.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exact branch failed query={Query}", ctx.Query);
            return 0;
        }
    }

    private async Task<(List<Guid> chunkIds, List<Guid> symbolIds)> GetExactCandidatesAsync(string query, Guid[]? repoIds, string[]? languages, string? symbolKind, CancellationToken ct)
    {
        var symbolIds = (await _symbols.FindExactIdsAsync(query, repoIds, languages, symbolKind, ct).ConfigureAwait(false)).ToList();
        if (symbolIds.Count == 0) return (new List<Guid>(), new List<Guid>());
        var chunkIds = (await _chunks.GetChunkIdsBySymbolIdsAsync(symbolIds, ct).ConfigureAwait(false)).ToList();
        return (chunkIds, symbolIds);
    }
}
