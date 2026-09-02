using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Indexer;

/// <summary>
/// Persistence для indexing_jobs (indexing.md §4, storage.md §2).
/// Хранит JobType/JobStatus как строки, progress 0..1.
/// </summary>
public sealed class IndexingJobStore : IIndexingJobStore
{
    private readonly IDbConnectionFactory _factory;

    public IndexingJobStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(IndexingJob job, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
INSERT INTO indexing_jobs(id, repository_id, type, status, started_at, finished_at, progress, error,
                          total_items, items_done, phase, current_file, from_commit, to_commit)
VALUES (@id,@repo,@type,@status,@started,@finished,@progress,@error,
        @total,@done,@phase,@currentFile,@fromCommit,@toCommit)
ON CONFLICT(id) DO UPDATE SET
  repository_id=excluded.repository_id, type=excluded.type, status=excluded.status,
  started_at=excluded.started_at, finished_at=excluded.finished_at, progress=excluded.progress, error=excluded.error,
  total_items=excluded.total_items, items_done=excluded.items_done, phase=excluded.phase,
  current_file=excluded.current_file, from_commit=excluded.from_commit, to_commit=excluded.to_commit;";
        await conn.ExecuteAsync(sql, new
        {
            id = job.Id.ToString(),
            repo = job.RepositoryId.ToString(),
            type = job.Type.ToString(),
            status = job.Status.ToString(),
            started = job.StartedAt.ToString("o"),
            finished = job.FinishedAt?.ToString("o"),
            progress = job.Progress,
            error = job.Error,
            total = job.TotalItems,
            done = job.ItemsDone,
            phase = job.Phase,
            currentFile = job.CurrentFile,
            fromCommit = job.FromCommit,
            toCommit = job.ToCommit
        }).ConfigureAwait(false);
    }

    public async Task<IndexingJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<JobRow>("SELECT * FROM indexing_jobs WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<IndexingJob>> ListAsync(Guid? repositoryId = null, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        IEnumerable<JobRow> rows;
        if (repositoryId.HasValue)
            rows = await conn.QueryAsync<JobRow>("SELECT * FROM indexing_jobs WHERE repository_id=@repo ORDER BY started_at DESC;", new { repo = repositoryId.Value.ToString() }).ConfigureAwait(false);
        else
            rows = await conn.QueryAsync<JobRow>("SELECT * FROM indexing_jobs ORDER BY started_at DESC;").ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<IndexingJob>> ListByStatusAsync(JobStatus status, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<JobRow>("SELECT * FROM indexing_jobs WHERE status=@st ORDER BY started_at;", new { st = status.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task UpdateProgressAsync(Guid jobId, double progress, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("UPDATE indexing_jobs SET progress=@p WHERE id=@id;", new { p = progress, id = jobId.ToString() }).ConfigureAwait(false);
    }

    /// <summary>Progress + детали (total/done/phase/current_file) одним UPDATE — для частых обновлений из воркеров.</summary>
    public async Task UpdateProgressAsync(IndexingJob job, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(@"
UPDATE indexing_jobs SET
  progress=@progress, total_items=@total, items_done=@done, phase=@phase, current_file=@currentFile
WHERE id=@id;", new
        {
            progress = job.Progress,
            total = job.TotalItems,
            done = job.ItemsDone,
            phase = job.Phase,
            currentFile = job.CurrentFile,
            id = job.Id.ToString()
        }).ConfigureAwait(false);
    }

    private static IndexingJob Map(JobRow r)
    {
        var id = Guid.Parse(r.id);
        var repo = Guid.Parse(r.repository_id);
        var type = Enum.TryParse<JobType>(r.type, out var t) ? t : JobType.Index;
        var status = Enum.TryParse<JobStatus>(r.status, out var s) ? s : JobStatus.Queued;
        var started = DateTimeOffset.TryParse(r.started_at, out var dt) ? dt : DateTimeOffset.UtcNow;
        DateTimeOffset? finished = r.finished_at is not null && DateTimeOffset.TryParse(r.finished_at, out var fin) ? fin : null;
        var from = r.fromCommit ?? r.from_commit;
        var to = r.toCommit ?? r.to_commit;
        var progress = r.progress ?? (status == JobStatus.Completed ? 1 : 0);
        return IndexingJob.Rehydrate(id, repo, type, status, started, finished, progress, r.error, from, to,
            r.total_items, r.items_done, r.phase, r.current_file);
    }

    private sealed class JobRow
    {
        public string id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string type { get; set; } = "";
        public string status { get; set; } = "";
        public string? started_at { get; set; }
        public string? finished_at { get; set; }
        public double? progress { get; set; }
        public string? error { get; set; }
        public int? total_items { get; set; }
        public int? items_done { get; set; }
        public string? phase { get; set; }
        public string? current_file { get; set; }
        // Колонки from_commit/to_commit (миграция 006); Dapper мапит both-названия, fallback ниже.
        public string? fromCommit { get; set; }
        public string? toCommit { get; set; }
        public string? from_commit { get; set; }
        public string? to_commit { get; set; }
    }
}
