namespace CodeRag.Domain.Enums;

/// <summary>Тип связи между символами (relations.relation_type).</summary>
public enum RelationType
{
    Calls = 0,
    Inherits = 1,
    Implements = 2,
    References = 3,
    Contains = 4,
    Imports = 5,
    Overrides = 6,
    Uses = 7,
    DependsOn = 8
}
