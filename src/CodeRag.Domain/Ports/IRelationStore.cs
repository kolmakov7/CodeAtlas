using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;

namespace CodeRag.Domain.Ports;

/// <summary>
/// Хранилище связей между символами (таблица relations).
/// </summary>
public interface IRelationStore
{
    /// <summary>Upsert batch of relations, tagging each with the owning repository.</summary>
    Task UpsertBatchAsync(Guid repositoryId, IEnumerable<Relation> relations, CancellationToken ct = default);

    /// <summary>Delete all relations belonging to a repository (repo deletion path).</summary>
    Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>Delete relations whose endpoints are symbols of a given language in a repository.</summary>
    Task DeleteByRepositoryAndLanguageAsync(Guid repositoryId, string language, CancellationToken ct = default);

    /// <summary>Atomically replace all relations of a language in a repository (delete + upsert).</summary>
    Task ReplaceForLanguageAsync(Guid repositoryId, string language, IEnumerable<Relation> relations, CancellationToken ct = default);

    Task<IReadOnlyList<Relation>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    Task<IReadOnlyList<Relation>> GetBySymbolAsync(
        Guid symbolId,
        RelationDirection direction = RelationDirection.Both,
        Guid? repositoryId = null,
        IReadOnlyList<RelationType>? types = null,
        CancellationToken ct = default);
}
