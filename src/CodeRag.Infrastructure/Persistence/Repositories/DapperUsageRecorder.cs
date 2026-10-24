using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Persistence.Repositories;

/// <summary>
/// Best-effort per-object usage recording into <c>symbol_usage</c>. Fire-and-forget: a failed
/// write must never surface into the request path (mirrors DapperMcpUsageRecorder).
/// </summary>
public sealed class DapperUsageRecorder : IUsageRecorder
{
    private readonly IDbConnectionFactory _factory;

    public DapperUsageRecorder(IDbConnectionFactory factory) => _factory = factory;

    public void RecordSymbol(string usageType, string source, Guid symbolId)
        => RecordSymbols(usageType, source, new[] { symbolId });

    public void RecordSymbols(string usageType, string source, IEnumerable<Guid> symbolIds)
    {
        var ids = symbolIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        _ = Task.Run(() => RecordSymbolsAsync(usageType, source, ids, CancellationToken.None));
    }

    public async Task RecordSymbolsAsync(string usageType, string source, IEnumerable<Guid> symbolIds, CancellationToken ct = default)
    {
        var ids = symbolIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        try
        {
            using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
            var at = DateTimeOffset.UtcNow.ToString("o");
            // Derive repository_id from the symbol itself; skip unknown ids (SELECT ... WHERE).
            const string sql = @"
INSERT INTO symbol_usage(symbol_id, repository_id, usage_type, source, count, last_used_at)
SELECT s.id, s.repository_id, @type, @source, 1, @at FROM symbols s WHERE s.id = @id
ON CONFLICT(symbol_id, usage_type, source) DO UPDATE SET
  count = symbol_usage.count + 1,
  last_used_at = excluded.last_used_at,
  repository_id = excluded.repository_id;";
            foreach (var id in ids)
                await conn.ExecuteAsync(sql, new { id = id.ToString(), type = usageType, source, at }).ConfigureAwait(false);
        }
        catch
        {
            // usage is best-effort — swallow
        }
    }
}
