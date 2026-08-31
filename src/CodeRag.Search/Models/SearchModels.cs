namespace CodeRag.Search.Models;

public enum SearchMode
{
    Hybrid = 0,
    FullText = 1,
    Semantic = 2,
    Symbol = 3
}

public sealed record SearchRequest(
    string Query,
    Guid[]? RepositoryIds = null,
    string[]? Languages = null,
    string? SymbolKind = null,
    SearchMode Mode = SearchMode.Hybrid,
    int Limit = 20,
    int Offset = 0,
    double? MinScore = null);

public sealed record FindSymbolRequest(
    string Query,
    Guid? RepositoryId = null,
    string? Language = null,
    string? SymbolKind = null,
    int Limit = 20,
    int Offset = 0);

public sealed record GetCodeRequest(
    Guid RepositoryId,
    string Path,
    int? StartLine = null,
    int? EndLine = null);

public sealed record SourceSlice(
    Guid RepositoryId,
    Guid FileId,
    string Path,
    string Language,
    int StartLine,
    int EndLine,
    int TotalLines,
    string Content,
    string Preview);

public sealed record SearchResult(
    IReadOnlyList<SearchResultItem> Items,
    int Total,
    IReadOnlyList<string> Warnings)
{
    public long ElapsedMs { get; init; }
}

public sealed record SearchResultItem(
    Guid RepositoryId,
    Guid FileId,
    Guid? SymbolId,
    string Language,
    string FilePath,
    int StartLine,
    int EndLine,
    double Score,
    string Preview,
    string Origin)
{
    public Guid ChunkId { get; init; }
    public string? SymbolName { get; init; }
    public string? Fqn { get; init; }
    public string? RepositoryName { get; init; }
}

public sealed record Symbol(
    Guid Id,
    Guid RepositoryId,
    Guid FileId,
    string Language,
    string Kind,
    string Name,
    string? Fqn,
    string? Namespace,
    string? Signature,
    int? StartLine,
    int? EndLine,
    string FilePath);

public sealed record Reference(
    Guid FromSymbolId,
    Guid ToSymbolId,
    string RelationType);

public sealed record RelationEntry(
    Guid FromSymbolId,
    Guid ToSymbolId,
    string RelationType,
    Symbol? FromSymbol,
    Symbol? ToSymbol);
