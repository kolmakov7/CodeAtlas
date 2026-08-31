namespace CodeRag.Analyzers.Abstractions;

/// <summary>Реляция между символами (§16 storage.md).</summary>
public sealed record Relation(
    Guid FromSymbolId,
    Guid ToSymbolId,
    string RelationType);

/// <summary>Типы реляций.</summary>
public static class RelationType
{
    public const string Defines = "Defines";
    public const string Contains = "Contains";
    public const string References = "References";
    public const string Calls = "Calls";
    public const string Implements = "Implements";
    public const string Inherits = "Inherits";
    public const string Returns = "Returns";
    public const string ParameterType = "ParameterType";
    public const string FieldType = "FieldType";
    public const string PropertyType = "PropertyType";
    public const string Creates = "Creates";
    public const string Overrides = "Overrides";
    public const string DependsOn = "DependsOn";
}
