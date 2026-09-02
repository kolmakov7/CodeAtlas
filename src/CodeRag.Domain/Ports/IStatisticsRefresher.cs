namespace CodeRag.Domain.Ports;

/// <summary>
/// Maintains precomputed statistics snapshots (repository_stats / global_stats).
/// Refreshes are relatively cheap aggregate queries, so they run off the read path:
/// once at startup and after indexing jobs change the index.
/// </summary>
public interface IStatisticsRefresher
{
    /// <summary>Recomputes and upserts the snapshot for a single repository.</summary>
    Task RefreshRepositoryAsync(Guid repositoryId, CancellationToken ct = default);

    /// <summary>Recomputes the global snapshot (aggregates repository snapshots + jobs).</summary>
    Task RefreshGlobalAsync(CancellationToken ct = default);

    /// <summary>
    /// Ensures snapshots exist: refreshes all repositories + global if the
    /// snapshot tables are empty (e.g. first start after migration, or legacy DB).
    /// </summary>
    Task EnsureAsync(CancellationToken ct = default);
}
