using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class EntityLinkStore : IEntityLinkStore
{
    private readonly IDbConnectionFactory _factory;

    public EntityLinkStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(EntityLink link, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"INSERT INTO entity_links(id, from_type, from_id, to_type, to_id, relation, note, created_by, created_at)
              VALUES(@id, @fromType, @fromId, @toType, @toId, @relation, @note, @createdBy, @createdAt)
              ON CONFLICT(from_type, from_id, to_type, to_id, relation) DO UPDATE SET
                note=excluded.note;",
            Params(link)).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync("DELETE FROM entity_links WHERE id=@id;", new { id = id.ToString() }).ConfigureAwait(false) > 0;
    }

    public async Task<bool> RemoveAsync(
        string fromType, Guid fromId, string toType, Guid toId, string relation, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(
            @"DELETE FROM entity_links
              WHERE from_type=@fromType AND from_id=@fromId AND to_type=@toType AND to_id=@toId AND relation=@relation;",
            new
            {
                fromType = fromType.ToLowerInvariant(),
                fromId = fromId.ToString(),
                toType = toType.ToLowerInvariant(),
                toId = toId.ToString(),
                relation = relation.ToLowerInvariant()
            }).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<EntityLink>> GetOutgoingAsync(
        string fromType, Guid fromId, CancellationToken ct = default)
        => await ListAsync(fromType, fromId, null, null, null, 500, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EntityLink>> GetIncomingAsync(
        string toType, Guid toId, CancellationToken ct = default)
        => await ListAsync(null, null, toType, toId, null, 500, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EntityLink>> ListAsync(
        string? fromType = null,
        Guid? fromId = null,
        string? toType = null,
        Guid? toId = null,
        string? relation = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var ps = new DynamicParameters();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(fromType))
        {
            where.Add("from_type=@fromType");
            ps.Add("fromType", fromType.ToLowerInvariant());
        }
        if (fromId is not null)
        {
            where.Add("from_id=@fromId");
            ps.Add("fromId", fromId.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(toType))
        {
            where.Add("to_type=@toType");
            ps.Add("toType", toType.ToLowerInvariant());
        }
        if (toId is not null)
        {
            where.Add("to_id=@toId");
            ps.Add("toId", toId.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(relation))
        {
            where.Add("relation=@relation");
            ps.Add("relation", relation.ToLowerInvariant());
        }
        ps.Add("limit", Math.Clamp(limit, 1, 1000));
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var rows = await conn.QueryAsync<LinkRow>(
            $"SELECT * FROM entity_links {whereSql} ORDER BY created_at DESC LIMIT @limit;", ps).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    private static object Params(EntityLink link) => new
    {
        id = link.Id.ToString(),
        fromType = link.FromType,
        fromId = link.FromId.ToString(),
        toType = link.ToType,
        toId = link.ToId.ToString(),
        relation = link.Relation,
        note = link.Note,
        createdBy = link.CreatedBy,
        createdAt = link.CreatedAt.ToString("o")
    };

    private static EntityLink Map(LinkRow r) => EntityLink.Rehydrate(
        Guid.Parse(r.id), r.from_type, Guid.Parse(r.from_id), r.to_type, Guid.Parse(r.to_id),
        r.relation, r.note, r.created_by, DateTimeOffset.Parse(r.created_at));

    private sealed class LinkRow
    {
        public string id { get; set; } = "";
        public string from_type { get; set; } = "";
        public string from_id { get; set; } = "";
        public string to_type { get; set; } = "";
        public string to_id { get; set; } = "";
        public string relation { get; set; } = "";
        public string? note { get; set; }
        public string? created_by { get; set; }
        public string created_at { get; set; } = "";
    }
}
