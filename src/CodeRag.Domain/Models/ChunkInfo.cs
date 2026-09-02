namespace CodeRag.Domain.Models;

/// <summary>
/// Read-only projection of a chunk joined with its file, repository and symbol for search results.
/// Crosses the Domain → Search boundary; intentionally a flat DTO (no entities rehydrated).
/// </summary>
public sealed record ChunkInfo(
    Guid ChunkId,
    Guid RepositoryId,
    Guid FileId,
    Guid? SymbolId,
    string? Language,
    string? FileLanguage,
    string? SymbolLanguage,
    string FilePath,
    int? StartLine,
    int? EndLine,
    string Source,
    string? SymbolName,
    string? Fqn,
    string? SymbolKind,
    string? RepositoryName);
