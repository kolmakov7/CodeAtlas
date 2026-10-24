using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public static class EntityTypes
{
    public const string Task = "task";
    public const string Knowledge = "knowledge";
    public const string Symbol = "symbol";
    public const string File = "file";
    public const string Repository = "repository";
    public const string Project = "project";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Task, Knowledge, Symbol, File, Repository, Project
    };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

public static class LinkRelations
{
    public const string Relates = "relates";
    public const string DependsOn = "depends_on";
    public const string Blocks = "blocks";
    public const string DerivedFrom = "derived_from";
    public const string Supersedes = "supersedes";
    public const string Contradicts = "contradicts";
    public const string Implements = "implements";
    public const string Source = "source";
    public const string Mentions = "mentions";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Relates, DependsOn, Blocks, DerivedFrom, Supersedes, Contradicts, Implements, Source, Mentions
    };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);
}

public sealed class EntityLink
{
    public Guid Id { get; private set; }
    public string FromType { get; private set; } = "";
    public Guid FromId { get; private set; }
    public string ToType { get; private set; } = "";
    public Guid ToId { get; private set; }
    public string Relation { get; private set; } = "";
    public string? Note { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private EntityLink(Guid id)
    {
        Id = id;
    }

    public static EntityLink Create(
        Guid id,
        string fromType,
        Guid fromId,
        string toType,
        Guid toId,
        string relation,
        string? note = null,
        string? createdBy = null,
        DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("EntityLink Id must not be empty.");
        if (!EntityTypes.IsValid(fromType)) throw new DomainValidationException($"Unknown fromType '{fromType}'.");
        if (!EntityTypes.IsValid(toType)) throw new DomainValidationException($"Unknown toType '{toType}'.");
        if (fromId == Guid.Empty || toId == Guid.Empty) throw new DomainValidationException("Link endpoints must not be empty.");
        if (!LinkRelations.IsValid(relation)) throw new DomainValidationException($"Unknown relation '{relation}'.");
        if (string.Equals(fromType, toType, StringComparison.OrdinalIgnoreCase) && fromId == toId)
            throw new DomainValidationException("An entity cannot link to itself.");

        return new EntityLink(id)
        {
            FromType = fromType.Trim().ToLowerInvariant(),
            FromId = fromId,
            ToType = toType.Trim().ToLowerInvariant(),
            ToId = toId,
            Relation = relation.Trim().ToLowerInvariant(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    }

    public static EntityLink Rehydrate(
        Guid id, string fromType, Guid fromId, string toType, Guid toId, string relation,
        string? note, string? createdBy, DateTimeOffset createdAt)
        => new(id)
        {
            FromType = fromType,
            FromId = fromId,
            ToType = toType,
            ToId = toId,
            Relation = relation,
            Note = note,
            CreatedBy = createdBy,
            CreatedAt = createdAt
        };
}
