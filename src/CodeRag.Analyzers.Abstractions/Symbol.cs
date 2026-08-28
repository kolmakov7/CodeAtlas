namespace CodeRag.Analyzers.Abstractions;

/// <summary>
/// Легковесный DTO символа, совместимый с Domain/storage.md (§16).
/// Анализатор генерирует <see cref="Id"/> (Guid.NewGuid) — Indexer переиспользует или перемаппит.
/// Поля RepositoryId/ProjectId/FileId не включаются — заполняются в Indexer из FileContext.
/// </summary>
public sealed record Symbol(
    Guid Id,
    string Language,
    string Kind,
    string Name,
    string? Fqn,
    string? Namespace,
    Guid? ParentSymbolId,
    int StartLine,
    int EndLine,
    int StartColumn,
    int EndColumn,
    string? Signature);

/// <summary>Kind символа. Расширяемо без breaking change — анализатор может отдать строку напрямую.</summary>
public static class SymbolKind
{
    public const string Namespace = "Namespace";
    public const string Class = "Class";
    public const string Interface = "Interface";
    public const string Record = "Record";
    public const string Struct = "Struct";
    public const string Enum = "Enum";
    public const string EnumMember = "EnumMember";
    public const string Method = "Method";
    public const string Constructor = "Constructor";
    public const string Function = "Function";
    public const string Property = "Property";
    public const string Field = "Field";
    public const string Event = "Event";
    public const string Parameter = "Parameter";
    public const string Variable = "Variable";
    public const string Constant = "Constant";
    // SQL / VBS
    public const string Procedure = "Procedure";
    public const string View = "View";
    public const string Table = "Table";
    public const string Trigger = "Trigger";
    public const string Sub = "Sub";
    public const string Module = "Module";
}
