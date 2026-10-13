using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;

namespace CodeRag.Domain.Ports;

/// <summary>
/// Persistence for generated documentation (table <c>documentation</c>).
/// Identity is (RepositoryId, TargetType, TargetRef).
/// </summary>
public interface IDocumentationStore
{
    /// <summary>Insert or update the entry keyed by (RepositoryId, TargetType, TargetRef).</summary>
    Task UpsertAsync(Documentation doc, CancellationToken ct = default);

    Task<Documentation?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lookup by stable identity. <paramref name="targetId"/> is ignored except to disambiguate when ref is null.</summary>
    Task<Documentation?> GetByTargetAsync(
        Guid repositoryId,
        DocumentationTargetType targetType,
        string targetRef,
        CancellationToken ct = default);

    /// <summary>All documentation entries for a repository (any target type), most recently updated first.</summary>
    Task<IReadOnlyList<Documentation>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>Entries for a repository filtered to one target type.</summary>
    Task<IReadOnlyList<Documentation>> GetByRepositoryAndTypeAsync(
        Guid repositoryId,
        DocumentationTargetType targetType,
        CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete every entry for a repository (repository removal path).</summary>
    Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>Set status on every row for a repository that is currently not in the given set.</summary>
    Task MarkStaleByRepositoryAsync(Guid repositoryId, CancellationToken ct = default);
}
