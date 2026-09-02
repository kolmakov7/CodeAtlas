using CodeRag.Search.Models;

namespace CodeRag.Search.Strategies;

/// <summary>
/// Стратегия ветки поиска (SRP, OCP): каждая ветка (exact/fts/vector/structural) изолирована.
/// Добавление нового источника кандидатов требует только нового класса, без правки orchestrator.
/// </summary>
public interface ISearchBranch
{
    SearchOrigin Origin { get; }
    bool ShouldRun(SearchMode mode);
    Task<int> ContributeAsync(SearchBranchContext ctx, Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct);
}
