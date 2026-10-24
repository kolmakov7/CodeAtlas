using CodeRag.Domain.Entities;
using CodeRag.Domain.Ports;
using Dapper;

namespace CodeRag.Infrastructure.Stores;

public sealed class EntityHistoryStore : IEntityHistoryStore
{
    private readonly IDbConnectionFactory _factory;

    public EntityHistoryStore(IDbConnectionFactory factory) => _factory = factory;

    public async Task AddAsync(EntityHistoryEntry entry, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            @"INSERT INTO entity_history(entity_type, entity_id, at, actor, action, version_from, version_to, changes)
              VALUES(@entityType, @entityId, @at, @actor, @action, @versionFrom, @versionTo, @changes);",
            new
            {
                entityType = entry.EntityType,
                entityId = entry.EntityId.ToString(),
                at = entry.At.ToString("o"),
                actor = entry.Actor,
                action = entry.Action,
                versionFrom = entry.VersionFrom,
                versionTo = entry.VersionTo,
                changes = entry.Changes
            }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EntityHistoryEntry>> ListAsync(
        string entityType, Guid entityId, int limit = 100, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<HistoryRow>(
            @"SELECT * FROM entity_history
              WHERE entity_type=@type AND entity_id=@id
              ORDER BY at DESC, id DESC LIMIT @limit;",
            new
            {
                type = entityType.ToLowerInvariant(),
                id = entityId.ToString(),
                limit = Math.Clamp(limit, 1, 500)
            }).ConfigureAwait(false);
        return rows.Select(r => EntityHistoryEntry.Rehydrate(
            r.id, r.entity_type, Guid.Parse(r.entity_id), DateTimeOffset.Parse(r.at), r.actor,
            r.action, r.version_from, r.version_to, r.changes)).ToList();
    }

    private sealed class HistoryRow
    {
        public long id { get; set; }
        public string entity_type { get; set; } = "";
        public string entity_id { get; set; } = "";
        public string at { get; set; } = "";
        public string? actor { get; set; }
        public string action { get; set; } = "";
        public int? version_from { get; set; }
        public int? version_to { get; set; }
        public string? changes { get; set; }
    }
}
