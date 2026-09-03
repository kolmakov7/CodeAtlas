using CodeRag.Domain.Entities;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Models;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class RelationStore : IRelationStore
{
    private readonly IDbConnectionFactory _factory;
    public RelationStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertBatchAsync(Guid repositoryId, IEnumerable<Relation> relations, CancellationToken ct = default)
    {
        var list = relations.ToList();
        if (list.Count == 0) return;
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            const string sql = @"
INSERT INTO relations(from_symbol_id, to_symbol_id, relation_type, repository_id)
VALUES (@from, @to, @type, @repo)
ON CONFLICT(from_symbol_id, to_symbol_id, relation_type) DO UPDATE SET
  repository_id = excluded.repository_id;";
            foreach (var r in list)
            {
                await conn.ExecuteAsync(sql, new
                {
                    from = r.FromSymbolId.ToString(),
                    to = r.ToSymbolId.ToString(),
                    type = r.RelationType.ToString(),
                    repo = repositoryId.ToString()
                }, transaction: tx).ConfigureAwait(false);
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task DeleteByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM relations WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
    }

    public async Task DeleteByRepositoryAndLanguageAsync(Guid repositoryId, string language, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        const string sql = @"
DELETE FROM relations
WHERE repository_id=@repo AND (
    from_symbol_id IN (SELECT id FROM symbols WHERE repository_id=@repo AND language=@lang)
    OR to_symbol_id IN (SELECT id FROM symbols WHERE repository_id=@repo AND language=@lang)
);";
        await conn.ExecuteAsync(sql, new { repo = repositoryId.ToString(), lang = language }).ConfigureAwait(false);
    }

    public async Task ReplaceForLanguageAsync(Guid repositoryId, string language, IEnumerable<Relation> relations, CancellationToken ct = default)
    {
        var list = relations.ToList();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        using var tx = conn.BeginTransaction();
        try
        {
            const string del = @"
DELETE FROM relations
WHERE repository_id=@repo AND (
    from_symbol_id IN (SELECT id FROM symbols WHERE repository_id=@repo AND language=@lang)
    OR to_symbol_id IN (SELECT id FROM symbols WHERE repository_id=@repo AND language=@lang)
);";
            await conn.ExecuteAsync(del, new { repo = repositoryId.ToString(), lang = language }, transaction: tx).ConfigureAwait(false);

            if (list.Count > 0)
            {
                const string sql = @"
INSERT INTO relations(from_symbol_id, to_symbol_id, relation_type, repository_id)
VALUES (@from, @to, @type, @repo)
ON CONFLICT(from_symbol_id, to_symbol_id, relation_type) DO UPDATE SET
  repository_id = excluded.repository_id;";
                foreach (var r in list)
                {
                    await conn.ExecuteAsync(sql, new
                    {
                        from = r.FromSymbolId.ToString(),
                        to = r.ToSymbolId.ToString(),
                        type = r.RelationType.ToString(),
                        repo = repositoryId.ToString()
                    }, transaction: tx).ConfigureAwait(false);
                }
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<Relation>> GetByRepositoryAsync(Guid repositoryId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RelRow>("SELECT * FROM relations WHERE repository_id=@repo;", new { repo = repositoryId.ToString() }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Relation>> GetBySymbolAsync(
        Guid symbolId,
        RelationDirection direction = RelationDirection.Both,
        Guid? repositoryId = null,
        IReadOnlyList<RelationType>? types = null,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);

        string dirSql = direction switch
        {
            RelationDirection.Outgoing => "from_symbol_id=@sid",
            RelationDirection.Incoming => "to_symbol_id=@sid",
            _ => "(from_symbol_id=@sid OR to_symbol_id=@sid)"
        };

        var sql = $"SELECT * FROM relations WHERE {dirSql}";
        if (repositoryId is not null) sql += " AND repository_id=@repo";
        if (types is not null && types.Count > 0) sql += " AND relation_type IN (@types)";

        var typeStrings = types is null ? Array.Empty<string>() : types.Select(t => t.ToString()).ToArray();
        var rows = await conn.QueryAsync<RelRow>(sql, new { sid = symbolId.ToString(), repo = repositoryId?.ToString(), types = typeStrings }).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<RelationEndpoints>> QueryEndpointsAsync(
        Guid symbolId,
        RelationDirection direction = RelationDirection.Both,
        IReadOnlyCollection<string>? relationTypeNames = null,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        string dirSql = direction switch
        {
            RelationDirection.Outgoing => "from_symbol_id=@sid",
            RelationDirection.Incoming => "to_symbol_id=@sid",
            _ => "(from_symbol_id=@sid OR to_symbol_id=@sid)"
        };
        var sql = $"SELECT from_symbol_id, to_symbol_id, relation_type FROM relations WHERE {dirSql}";
        var p = new DynamicParameters(); p.Add("sid", symbolId.ToString());
        if (relationTypeNames is { Count: > 0 })
        {
            var arr = relationTypeNames.ToArray();
            sql += " AND relation_type IN (" + string.Join(",", arr.Select((_, i) => $"@t{i}")) + ")";
            for (int i = 0; i < arr.Length; i++) p.Add($"t{i}", arr[i]);
        }
        var rows = (await conn.QueryAsync<EndpointsRow>(sql, p).ConfigureAwait(false)).ToList();
        return rows.Select(r => new RelationEndpoints(Guid.Parse(r.from_symbol_id), Guid.Parse(r.to_symbol_id), r.relation_type)).ToList();
    }

    public async Task<IReadOnlyList<RelationEndpoints>> QueryEndpointsBySymbolIdsAsync(
        IReadOnlyCollection<Guid> symbolIds,
        CancellationToken ct = default)
    {
        if (symbolIds.Count == 0) return Array.Empty<RelationEndpoints>();
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ids = symbolIds.Select(g => g.ToString()).ToArray();
        var ph = string.Join(",", ids.Select((_, i) => $"@e{i}"));
        var ph2 = string.Join(",", ids.Select((_, i) => $"@e2{i}"));
        var p = new DynamicParameters();
        for (int i = 0; i < ids.Length; i++) { p.Add($"e{i}", ids[i]); p.Add($"e2{i}", ids[i]); }
        var sql = $"SELECT from_symbol_id, to_symbol_id, relation_type FROM relations WHERE from_symbol_id IN ({ph}) OR to_symbol_id IN ({ph2})";
        var rows = (await conn.QueryAsync<EndpointsRow>(sql, p).ConfigureAwait(false)).ToList();
        return rows.Select(r => new RelationEndpoints(Guid.Parse(r.from_symbol_id), Guid.Parse(r.to_symbol_id), r.relation_type)).ToList();
    }

    private static Relation Map(RelRow r)
    {
        var type = Enum.TryParse<RelationType>(r.relation_type, out var t) ? t : RelationType.References;
        return new Relation(Guid.Parse(r.from_symbol_id), Guid.Parse(r.to_symbol_id), type);
    }

    private sealed class RelRow
    {
        public string from_symbol_id { get; set; } = "";
        public string to_symbol_id { get; set; } = "";
        public string relation_type { get; set; } = "";
        public string? repository_id { get; set; }
    }

    private sealed class EndpointsRow
    {
        public string from_symbol_id { get; set; } = "";
        public string to_symbol_id { get; set; } = "";
        public string relation_type { get; set; } = "";
    }
}
