using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class EntityHistoryEntry
{
    public long Id { get; private set; }
    public string EntityType { get; private set; } = "";
    public Guid EntityId { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string? Actor { get; private set; }
    public string Action { get; private set; } = "";
    public int? VersionFrom { get; private set; }
    public int? VersionTo { get; private set; }
    public string? Changes { get; private set; }

    private EntityHistoryEntry(long id)
    {
        Id = id;
    }

    public static EntityHistoryEntry Create(
        string entityType,
        Guid entityId,
        string action,
        string? actor = null,
        int? versionFrom = null,
        int? versionTo = null,
        string? changes = null,
        DateTimeOffset? at = null)
    {
        if (!EntityTypes.IsValid(entityType)) throw new DomainValidationException($"Unknown entityType '{entityType}'.");
        if (entityId == Guid.Empty) throw new DomainValidationException("EntityId must not be empty.");
        if (string.IsNullOrWhiteSpace(action)) throw new DomainValidationException("History action must not be empty.");
        return new EntityHistoryEntry(0)
        {
            EntityType = entityType.Trim().ToLowerInvariant(),
            EntityId = entityId,
            At = at ?? DateTimeOffset.UtcNow,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            Action = action.Trim(),
            VersionFrom = versionFrom,
            VersionTo = versionTo,
            Changes = string.IsNullOrWhiteSpace(changes) ? null : changes
        };
    }

    public static EntityHistoryEntry Rehydrate(
        long id, string entityType, Guid entityId, DateTimeOffset at, string? actor,
        string action, int? versionFrom, int? versionTo, string? changes)
        => new(id)
        {
            EntityType = entityType,
            EntityId = entityId,
            At = at,
            Actor = actor,
            Action = action,
            VersionFrom = versionFrom,
            VersionTo = versionTo,
            Changes = changes
        };
}
