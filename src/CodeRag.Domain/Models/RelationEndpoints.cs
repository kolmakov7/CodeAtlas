namespace CodeRag.Domain.Models;

/// <summary>
/// Read-only projection of a single relation row (endpoints + raw type) for graph traversal.
/// Crosses the Domain → Search boundary; RelationType is the stored string (may be a non-canonical value).
/// </summary>
public sealed record RelationEndpoints(Guid FromSymbolId, Guid ToSymbolId, string RelationType);
