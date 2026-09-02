using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Search.Models;
using CodeRag.Search.Strategies;
using Microsoft.Extensions.Options;

namespace CodeRag.Search.Services;

public sealed class SearchRanker
{
    private readonly IOptionsMonitor<SearchOptions> _opts;
    public SearchRanker(IOptionsMonitor<SearchOptions> opts) => _opts = opts;

    public List<(CandidateAccumulator acc, ChunkInfo info, double finalScore)> Rank(
        List<(CandidateAccumulator acc, ChunkInfo info)> filtered,
        SearchMode mode)
    {
        var opts = _opts.CurrentValue;
        var ftsRanks = filtered.Where(x => x.acc.FtsRank.HasValue).Select(x => x.acc.FtsRank!.Value).ToList();
        double ftsMin = ftsRanks.Count > 0 ? ftsRanks.Min() : 0;
        double ftsMax = ftsRanks.Count > 0 ? ftsRanks.Max() : 0;
        bool ftsHasRange = ftsRanks.Count > 0 && Math.Abs(ftsMax - ftsMin) > 1e-9;

        var vecBounds = new Dictionary<EmbeddingDomain, (double Min, double Max, bool HasRange)>();
        foreach (var domain in new[] { EmbeddingDomain.Code, EmbeddingDomain.Text })
        {
            var scores = filtered.Where(x => x.acc.VectorScore.HasValue && x.acc.VectorDomain == domain).Select(x => x.acc.VectorScore!.Value).ToList();
            if (scores.Count == 0) continue;
            vecBounds[domain] = (scores.Min(), scores.Max(), Math.Abs(scores.Max() - scores.Min()) > 1e-9);
        }

        double NormVec(CandidateAccumulator acc)
        {
            if (!acc.VectorScore.HasValue) return 0;
            if (!vecBounds.TryGetValue(acc.VectorDomain, out var b)) return 1.0;
            var norm = !b.HasRange ? 1.0 : (acc.VectorScore.Value - b.Min) / (b.Max - b.Min);
            return acc.VectorDomain == EmbeddingDomain.Text ? norm * opts.TextDomainWeight : norm;
        }

        var scored = new List<(CandidateAccumulator acc, ChunkInfo info, double finalScore)>(filtered.Count);
        foreach (var (acc, info) in filtered)
        {
            if (mode == SearchMode.Semantic || mode == SearchMode.SemanticCode || mode == SearchMode.SemanticText)
            {
                var semScore = mode == SearchMode.Semantic ? NormVec(acc) : Math.Clamp(acc.VectorScore ?? 0.0, 0.0, 1.0);
                scored.Add((acc, info, semScore));
                continue;
            }
            double normFts = 0, normVec = 0;
            if (acc.FtsRank.HasValue)
            {
                if (!ftsHasRange) normFts = 1.0;
                else normFts = (ftsMax - acc.FtsRank.Value) / (ftsMax - ftsMin);
            }
            normVec = NormVec(acc);
            double structuralBoost = acc.HasStructural ? 1.0 : 0.0;
            double exactBonus = acc.HasExact ? opts.ExactBonus : 0.0;
            double finalScore = opts.LexicalWeight * normFts + opts.SemanticWeight * normVec + opts.StructuralWeight * structuralBoost + exactBonus;
            scored.Add((acc, info, finalScore));
        }
        return scored;
    }
}
