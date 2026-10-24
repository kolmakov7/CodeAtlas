using CodeRag.Domain.Models;

namespace CodeRag.Domain.Ports;

/// <summary>
/// Records per-object usage (which symbols are looked up / referenced / queried).
/// Best-effort and non-blocking: implementations must never throw into the hot path.
/// </summary>
public interface IUsageRecorder
{
    /// <summary>Fire-and-forget: record one usage event for each symbol id.</summary>
    void RecordSymbols(string usageType, string source, IEnumerable<Guid> symbolIds);

    /// <summary>Fire-and-forget: record one usage event for a single symbol.</summary>
    void RecordSymbol(string usageType, string source, Guid symbolId);

    /// <summary>Awaitable variant (tests / explicit flush).</summary>
    Task RecordSymbolsAsync(string usageType, string source, IEnumerable<Guid> symbolIds, CancellationToken ct = default);
}

/// <summary>Read-side of usage statistics.</summary>
public interface IUsageQueryService
{
    /// <summary>Top-used objects, optionally scoped to a repository, within the last <paramref name="days"/>.</summary>
    Task<IReadOnlyList<SymbolUsageInfo>> GetTopAsync(Guid? repositoryId, int limit, int days, CancellationToken ct = default);

    /// <summary>Aggregate usage score per symbol for a repository (drives documentation prioritisation).</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetScoreByRepositoryAsync(Guid repositoryId, int days, CancellationToken ct = default);
}
