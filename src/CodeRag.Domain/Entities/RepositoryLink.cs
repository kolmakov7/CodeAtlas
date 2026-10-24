using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>
/// A directed relationship between two repositories (table <c>repository_links</c>), e.g.
/// "app repo <c>depends_on</c> db repo". Drives cross-repository relation resolution.
/// Identity: (SourceRepositoryId, TargetRepositoryId, LinkType).
/// </summary>
public sealed class RepositoryLink
{
    public Guid Id { get; private set; }
    public Guid SourceRepositoryId { get; private set; }
    public Guid TargetRepositoryId { get; private set; }
    public string LinkType { get; private set; }

    /// <summary>Set for auto-suggested links (0..1); null for manual links.</summary>
    public double? Confidence { get; private set; }

    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private RepositoryLink(Guid id, Guid source, Guid target, string linkType)
    {
        Id = id;
        SourceRepositoryId = source;
        TargetRepositoryId = target;
        LinkType = linkType;
    }

    public static RepositoryLink Create(
        Guid id, Guid sourceRepositoryId, Guid targetRepositoryId, string linkType,
        double? confidence = null, string? note = null, DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("RepositoryLink Id must not be empty.");
        if (sourceRepositoryId == Guid.Empty) throw new DomainValidationException("SourceRepositoryId must not be empty.");
        if (targetRepositoryId == Guid.Empty) throw new DomainValidationException("TargetRepositoryId must not be empty.");
        if (sourceRepositoryId == targetRepositoryId) throw new DomainValidationException("A repository cannot link to itself.");
        if (string.IsNullOrWhiteSpace(linkType)) throw new DomainValidationException("LinkType must not be empty.");

        return new RepositoryLink(id, sourceRepositoryId, targetRepositoryId, linkType.Trim())
        {
            Confidence = confidence,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    }

    public static RepositoryLink Rehydrate(
        Guid id, Guid sourceRepositoryId, Guid targetRepositoryId, string linkType,
        double? confidence, string? note, DateTimeOffset createdAt)
        => new(id, sourceRepositoryId, targetRepositoryId, linkType)
        {
            Confidence = confidence,
            Note = note,
            CreatedAt = createdAt
        };
}
