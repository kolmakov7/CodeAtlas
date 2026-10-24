using CodeRag.Domain.Entities;
using CodeRag.Domain.Models;

namespace CodeRag.Domain.Ports;

public interface IKnowledgeStore
{
    Task CreateAsync(KnowledgeEntry entry, CancellationToken ct = default);
    Task<KnowledgeEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeEntry>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<KnowledgeEntry?> GetByExternalKeyAsync(Guid? projectId, string externalKey, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeEntry>> ListAsync(KnowledgeQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeEntry>> SearchLexicalAsync(
        string query, KnowledgeQuery filter, CancellationToken ct = default);
    Task<bool> UpdateAsync(KnowledgeEntry entry, int expectedVersion, CancellationToken ct = default);
    Task<int> CountAsync(KnowledgeQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeEntry>> GetEmbeddingCandidatesAsync(
        string model, string configHash, int limit = 100, CancellationToken ct = default);
    Task MarkEmbeddedAsync(
        Guid id, string model, string configHash, DateTimeOffset at, CancellationToken ct = default);
}
