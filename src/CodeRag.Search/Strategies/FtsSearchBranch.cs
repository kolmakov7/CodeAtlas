using CodeRag.Infrastructure.Search;
using CodeRag.Search.Models;
using Microsoft.Extensions.Logging;

namespace CodeRag.Search.Strategies;

public sealed class FtsSearchBranch : ISearchBranch
{
    public SearchOrigin Origin => SearchOrigin.Fts;
    private readonly FtsHelper _fts;
    private readonly ILogger<FtsSearchBranch> _logger;

    public FtsSearchBranch(FtsHelper fts, ILogger<FtsSearchBranch> logger)
    {
        _fts = fts;
        _logger = logger;
    }

    public bool ShouldRun(SearchMode mode) => mode == SearchMode.Hybrid || mode == SearchMode.FullText;

    public async Task<int> ContributeAsync(SearchBranchContext ctx, Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct)
    {
        try
        {
            var ftsResults = await GetFtsCandidatesAsync(ctx.Query, ctx.RepoFilter, ctx.Limit, ct).ConfigureAwait(false);
            foreach (var r in ftsResults)
            {
                if (!candidates.TryGetValue(r.ChunkId, out var acc))
                {
                    acc = new CandidateAccumulator(r.ChunkId);
                    candidates[r.ChunkId] = acc;
                }
                acc.Origins.Add(Origin);
                if (!acc.FtsRank.HasValue || r.Rank < acc.FtsRank.Value)
                    acc.FtsRank = r.Rank;
            }
            return ftsResults.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS branch failed query={Query}", ctx.Query);
            return 0;
        }
    }

    private async Task<IReadOnlyList<FtsResult>> GetFtsCandidatesAsync(string query, Guid[]? repoIds, int limit, CancellationToken ct)
    {
        int fetchLimit = Math.Max(limit * 3, 50);
        if (repoIds != null && repoIds.Length == 1)
            return await _fts.SearchAsync(query, fetchLimit, repoIds[0], ct).ConfigureAwait(false);
        if (repoIds != null && repoIds.Length > 1)
        {
            var all = await _fts.SearchAsync(query, fetchLimit * 2, null, ct).ConfigureAwait(false);
            var set = new HashSet<Guid>(repoIds);
            return all.Where(r => set.Contains(r.RepositoryId)).Take(fetchLimit).ToList();
        }
        return await _fts.SearchAsync(query, fetchLimit, null, ct).ConfigureAwait(false);
    }
}
