namespace CodeRag.Domain.Models;

/// <summary>A usage row for one object (symbol) aggregated across usage types/sources.</summary>
public sealed record SymbolUsageInfo(
    Guid SymbolId,
    Guid RepositoryId,
    string Name,
    string? Fqn,
    string Kind,
    string? FilePath,
    long UsageCount,
    DateTimeOffset LastUsedAt,
    double Score);
