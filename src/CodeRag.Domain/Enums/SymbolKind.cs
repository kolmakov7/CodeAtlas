namespace CodeRag.Domain.Enums;

/// <summary>Вид символа (symbols.kind). Расширяется анализаторами.</summary>
public enum SymbolKind
{
    Unknown = 0,
    Class = 1,
    Interface = 2,
    Record = 18,
    Event = 19,
    Struct = 3,
    Enum = 4,
    EnumMember = 5,
    Method = 6,
    Function = 7,
    Constructor = 8,
    Property = 9,
    Field = 10,
    Variable = 11,
    Constant = 12,
    Namespace = 13,
    Module = 14,
    Procedure = 15,
    View = 16,
    Table = 17
}
