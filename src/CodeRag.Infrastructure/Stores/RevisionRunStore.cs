using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class RevisionRunStore : IRevisionRunStore
{
    private readonly IDbConnectionFactory _factory;

    public RevisionRunStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(RevisionRun run, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"INSERT INTO revision_runs(id, trigger_type, status, started_at, finished_at, stats, error)
              VALUES(@id, @trigger, @status, @started, @finished, @stats, @error);",
            Params(run)).ConfigureAwait(false);
    }

    public async Task UpdateAsync(RevisionRun run, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"UPDATE revision_runs SET status=@status, finished_at=@finished, stats=@stats, error=@error
              WHERE id=@id;",
            Params(run)).ConfigureAwait(false);
    }

    public async Task<RevisionRun?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<RunRow>(
            "SELECT * FROM revision_runs WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<RevisionRun?> GetLatestAsync(CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<RunRow>(
            "SELECT * FROM revision_runs ORDER BY started_at DESC LIMIT 1;").ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<RevisionRun>> ListAsync(int limit = 20, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RunRow>(
            "SELECT * FROM revision_runs ORDER BY started_at DESC LIMIT @limit;",
            new { limit = Math.Clamp(limit, 1, 200) }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    private static object Params(RevisionRun r) => new
    {
        id = r.Id.ToString(),
        trigger = r.Trigger,
        status = r.Status,
        started = r.StartedAt.ToString("o"),
        finished = r.FinishedAt?.ToString("o"),
        stats = r.Stats,
        error = r.Error
    };

    private static RevisionRun Map(RunRow r) => RevisionRun.Rehydrate(
        Guid.Parse(r.id), r.trigger_type, r.status, DateTimeOffset.Parse(r.started_at),
        string.IsNullOrWhiteSpace(r.finished_at) ? null : DateTimeOffset.Parse(r.finished_at),
        r.stats, r.error);

    private sealed class RunRow
    {
        public string id { get; set; } = "";
        public string trigger_type { get; set; } = "";
        public string status { get; set; } = "";
        public string started_at { get; set; } = "";
        public string? finished_at { get; set; }
        public string? stats { get; set; }
        public string? error { get; set; }
    }
}
