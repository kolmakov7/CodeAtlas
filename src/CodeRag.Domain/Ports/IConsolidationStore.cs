using CodeRag.Domain.Entities;
using CodeRag.Domain.Models;

namespace CodeRag.Domain.Ports;

public interface IConsolidationStore
{
    Task<bool> CreateIfAbsentAsync(
        ConsolidationJob job,
        IReadOnlyList<ConsolidationJobItem> items,
        CancellationToken ct = default);
    Task<ConsolidationJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ConsolidationJob?> GetByDedupeKeyAsync(string dedupeKey, CancellationToken ct = default);
    Task<IReadOnlyList<ConsolidationJob>> ListAsync(ConsolidationJobQuery query, CancellationToken ct = default);
    Task<int> CountAsync(ConsolidationJobQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ConsolidationJobItem>> GetItemsAsync(Guid jobId, CancellationToken ct = default);
    Task<bool> UpdateAsync(ConsolidationJob job, int expectedVersion, CancellationToken ct = default);
    Task<ConsolidationApplyResult> ApplyAsync(
        ConsolidationJob job,
        IReadOnlyList<ConsolidationJobItem> items,
        KnowledgeEntry merged,
        int expectedJobVersion,
        CancellationToken ct = default);
    Task<ConsolidationApplyResult> UndoAsync(
        ConsolidationJob job,
        IReadOnlyList<ConsolidationJobItem> items,
        Guid resultKnowledgeId,
        int expectedJobVersion,
        CancellationToken ct = default);
}
