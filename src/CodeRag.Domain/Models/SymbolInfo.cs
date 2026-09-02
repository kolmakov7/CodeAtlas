namespace CodeRag.Domain.Models;

/// <summary>
/// Read-only projection of a symbol joined with its file path for search/listing.
/// Crosses the Domain → Search boundary; a flat DTO (no entity rehydration).
/// </summary>
public sealed record SymbolInfo(
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
