using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Persistence.Repositories;

/// <summary>Read-side of <c>symbol_usage</c>: top-used objects and per-repo usage scores.</summary>
public sealed class DapperUsageQueryService : IUsageQueryService
{
    private readonly IDbConnectionFactory _factory;

    public DapperUsageQueryService(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<SymbolUsageInfo>> GetTopAsync(Guid? repositoryId, int limit, int days, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650)).ToString("o");
        var sql = @"
SELECT u.symbol_id AS symbol_id, u.repository_id AS repository_id, s.name AS name, s.fqn AS fqn,
       s.kind AS kind, f.path AS file_path, SUM(u.count) AS usage_count, MAX(u.last_used_at) AS last_used_at
FROM symbol_usage u
JOIN symbols s ON s.id = u.symbol_id
LEFT JOIN files f ON f.id = s.file_id
WHERE u.last_used_at >= @since"
            + (repositoryId.HasValue ? " AND u.repository_id = @repo" : "") + @"
GROUP BY u.symbol_id, u.repository_id, s.name, s.fqn, s.kind, f.path
ORDER BY usage_count DESC, last_used_at DESC
LIMIT @limit;";
        var rows = await conn.QueryAsync<UsageRow>(sql, new { since, repo = repositoryId?.ToString(), limit = Math.Clamp(limit, 1, 500) }).ConfigureAwait(false);
        return rows.Select(r =>
        {
            var has = DateTimeOffset.TryParse(r.last_used_at, out var at);
            var count = (long)r.usage_count;
            return new SymbolUsageInfo(
                Guid.Parse(r.symbol_id), Guid.Parse(r.repository_id), r.name, r.fqn, r.kind, r.file_path,
                count, has ? at : DateTimeOffset.UtcNow, count);
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetScoreByRepositoryAsync(Guid repositoryId, int days, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 3650)).ToString("o");
        const string sql = @"
SELECT symbol_id, SUM(count) AS usage_count
FROM symbol_usage
WHERE repository_id = @repo AND last_used_at >= @since
GROUP BY symbol_id;";
        var rows = await conn.QueryAsync<ScoreRow>(sql, new { repo = repositoryId.ToString(), since }).ConfigureAwait(false);
        var result = new Dictionary<Guid, int>();
        foreach (var r in rows)
            if (Guid.TryParse(r.symbol_id, out var g)) result[g] = (int)r.usage_count;
        return result;
    }

    private sealed class ScoreRow
    {
        public string symbol_id { get; set; } = "";
        public long usage_count { get; set; }
    }

    private sealed class UsageRow
    {
        public string symbol_id { get; set; } = "";
        public string repository_id { get; set; } = "";
        public string name { get; set; } = "";
        public string? fqn { get; set; }
        public string kind { get; set; } = "";
        public string? file_path { get; set; }
        public long usage_count { get; set; }
        public string last_used_at { get; set; } = "";
    }
}
