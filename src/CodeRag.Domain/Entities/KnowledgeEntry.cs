using System.Security.Cryptography;
using System.Text;
using CodeRag.Domain.Enums;
using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

public sealed class KnowledgeEntry
{
    public Guid Id { get; private set; }
    public string Title { get; private set; } = "";
    public string Content { get; private set; } = "";
    public KnowledgeType Type { get; private set; }
    public Guid? ProjectId { get; private set; }
    public Guid? RepositoryId { get; private set; }
    public KnowledgeStatus Status { get; private set; }
    public string Source { get; private set; } = "agent";
    public string? SourceRef { get; private set; }
    public string? ExternalKey { get; private set; }
    public string ContentHash { get; private set; } = "";
    public string? CreatedBy { get; private set; }
    public string? Metadata { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ValidFrom { get; private set; }
    public DateTimeOffset? ValidUntil { get; private set; }
    public int Version { get; private set; } = 1;
    public string? EmbeddedModel { get; private set; }
    public string? EmbeddedConfigHash { get; private set; }
    public DateTimeOffset? EmbeddedAt { get; private set; }

    private KnowledgeEntry(Guid id)
    {
        Id = id;
    }

    public static KnowledgeEntry Create(
        Guid id,
        string title,
        string content,
        KnowledgeType type,
        Guid? projectId = null,
        Guid? repositoryId = null,
        KnowledgeStatus status = KnowledgeStatus.Active,
        string source = "agent",
        string? sourceRef = null,
        string? externalKey = null,
        string? createdBy = null,
        string? metadata = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Knowledge Id must not be empty.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainValidationException("Knowledge title must not be empty.");
        if (string.IsNullOrWhiteSpace(content)) throw new DomainValidationException("Knowledge content must not be empty.");
        if (validFrom is not null && validUntil is not null && validUntil < validFrom)
            throw new DomainValidationException("validUntil must not be before validFrom.");

        var now = createdAt ?? DateTimeOffset.UtcNow;
        var entity = new KnowledgeEntry(id)
        {
            Title = title.Trim(),
            Content = content.Trim(),
            Type = type,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            Status = status,
            Source = string.IsNullOrWhiteSpace(source) ? "agent" : source.Trim(),
            SourceRef = string.IsNullOrWhiteSpace(sourceRef) ? null : sourceRef.Trim(),
            ExternalKey = string.IsNullOrWhiteSpace(externalKey) ? null : externalKey.Trim(),
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            Metadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata,
            CreatedAt = now,
            UpdatedAt = now,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Version = 1
        };
        entity.ContentHash = ComputeHash(entity.Title, entity.Content);
        return entity;
    }

    public void UpdateContent(string title, string content, KnowledgeType type, string? metadata)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainValidationException("Knowledge title must not be empty.");
        if (string.IsNullOrWhiteSpace(content)) throw new DomainValidationException("Knowledge content must not be empty.");
        Title = title.Trim();
        Content = content.Trim();
        Type = type;
        Metadata = string.IsNullOrWhiteSpace(metadata) ? null : metadata;
        ContentHash = ComputeHash(Title, Content);
        EmbeddedAt = null;
        EmbeddedModel = null;
        EmbeddedConfigHash = null;
    }

    public void SetStatus(KnowledgeStatus status)
    {
        Status = status;
    }

    public void SetScope(Guid? projectId, Guid? repositoryId)
    {
        ProjectId = projectId;
        RepositoryId = repositoryId;
    }

    public void MarkEmbedded(string model, string configHash, DateTimeOffset? at = null)
    {
        EmbeddedModel = model;
        EmbeddedConfigHash = configHash;
        EmbeddedAt = at ?? DateTimeOffset.UtcNow;
    }

    public bool NeedsEmbedding(string model, string configHash)
        => EmbeddedAt is null || EmbeddedModel != model || EmbeddedConfigHash != configHash;

    public void BumpVersion(DateTimeOffset? at = null)
    {
        Version++;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public static string ComputeHash(string title, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(title + "\n" + content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static KnowledgeEntry Rehydrate(
        Guid id, string title, string content, KnowledgeType type,
        Guid? projectId, Guid? repositoryId, KnowledgeStatus status,
        string source, string? sourceRef, string? externalKey, string contentHash,
        string? createdBy, string? metadata, DateTimeOffset createdAt, DateTimeOffset updatedAt,
        DateTimeOffset? validFrom, DateTimeOffset? validUntil, int version,
        string? embeddedModel, string? embeddedConfigHash, DateTimeOffset? embeddedAt)
        => new(id)
        {
            Title = title,
            Content = content,
            Type = type,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            Status = status,
            Source = source,
            SourceRef = sourceRef,
            ExternalKey = externalKey,
            ContentHash = contentHash,
            CreatedBy = createdBy,
            Metadata = metadata,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Version = version,
            EmbeddedModel = embeddedModel,
            EmbeddedConfigHash = embeddedConfigHash,
            EmbeddedAt = embeddedAt
        };
}
