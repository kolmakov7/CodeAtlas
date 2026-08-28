using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>
/// Связь между символами (таблица relations, PK = from+to+type).
/// Value-object по ключу, но моделируется как сущность для удобства.
/// </summary>
public sealed class Relation : IEquatable<Relation>
{
    public Guid FromSymbolId { get; }
    public Guid ToSymbolId { get; }
    public RelationType RelationType { get; }

    public Relation(Guid fromSymbolId, Guid toSymbolId, RelationType relationType)
    {
        if (fromSymbolId == Guid.Empty) throw new DomainValidationException("FromSymbolId must not be empty.");
        if (toSymbolId == Guid.Empty) throw new DomainValidationException("ToSymbolId must not be empty.");
        if (fromSymbolId == toSymbolId && relationType != RelationType.Contains)
            throw new DomainValidationException("Relation cannot be self-referential except Contains.");

        FromSymbolId = fromSymbolId;
        ToSymbolId = toSymbolId;
        RelationType = relationType;
    }

    public bool Equals(Relation? other) =>
        other is not null && FromSymbolId == other.FromSymbolId && ToSymbolId == other.ToSymbolId && RelationType == other.RelationType;

    public override bool Equals(object? obj) => Equals(obj as Relation);
    public override int GetHashCode() => HashCode.Combine(FromSymbolId, ToSymbolId, RelationType);
}
