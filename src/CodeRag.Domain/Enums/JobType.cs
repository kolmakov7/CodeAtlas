namespace CodeRag.Domain.Enums;

/// <summary>Тип джобы индексации (indexing.md §4).</summary>
public enum JobType
{
    Sync = 0,
    Index = 1,
    Embedding = 2,
    Rebuild = 3
}
