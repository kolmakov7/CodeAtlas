using CodeRag.Domain.Enums;

namespace CodeRag.Domain.Ports;

/// <summary>
/// Computes a stable content fingerprint for a documentation target, used for staleness detection.
/// The fingerprint combines the target's own source with its direct dependencies (one hop),
/// built from existing index metadata (file hashes, chunk hashes, relations) — never by re-parsing.
/// </summary>
public interface ITargetContentHasher
{
    /// <summary>
    /// Compute the source hash for a target.
    /// <returns>
    /// A stable hex string, or <c>null</c> when the source cannot be resolved
    /// (e.g. no index data / file missing) — callers treat null as "not stale-able".
    /// </returns>
    /// </summary>
    Task<string?> ComputeAsync(
        Guid repositoryId,
        DocumentationTargetType targetType,
        string? targetRef,
        Guid? targetSymbolId,
        string? filePath,
        CancellationToken ct = default);

    /// <summary>
    /// Compute the source hash for every entry in <paramref name="existing"/> and report which
    /// are now stale (hash changed since generation). Returns a map of entry id → current hash.
    /// Entries whose current hash could not be computed are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ComputeManyAsync(
        Guid repositoryId,
        IEnumerable<CodeRag.Domain.Entities.Documentation> existing,
        CancellationToken ct = default);
}
